using System.Collections.Concurrent;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Singleton coordinator for the Interception driver loop, device enumeration,
/// selective per-slot filtering, loop-back stroke decisions, and direct hardware injection.
/// </summary>
public sealed partial class InterceptionDriverService
{
    public static InterceptionDriverService Instance { get; } = new();

    private const uint WaitTimeoutMs = 25;

    private readonly object _gate = new();
    private readonly List<ActiveSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, PendingDecision> _pendingDecisions = new(StringComparer.Ordinal);
    private readonly List<InterceptionPredicate> _activePredicates = new();
    private readonly InterceptionPredicate _allDevicesPredicate = static _ => 1;

    private IInterceptionBackend? _backend;
    private bool _ownsBackend;
    private string? _lastDllPath;
    private IReadOnlyList<InterceptionDeviceInfo> _cachedDevices = Array.Empty<InterceptionDeviceInfo>();

    private Thread? _workerThread;
    private CancellationTokenSource? _workerCts;
    private nint _activeContext;
    private volatile bool _filtersDirty;
    private volatile bool _panicTriggered;

    // Panic combo (Ctrl+Alt+Pause) modifier tracking on the driver loop thread.
    private bool _lCtrlDown;
    private bool _rCtrlDown;
    private bool _lAltDown;
    private bool _rAltDown;

    public event Action? StateChanged;

    private InterceptionDriverService()
    {
    }

    public bool IsPanicTriggered => _panicTriggered;

    /// <summary>
    /// Injects a mock <see cref="IInterceptionBackend"/> for automated unit tests,
    /// or passes null to restore lazy native DLL loading.
    /// </summary>
    public void SetBackendForTesting(IInterceptionBackend? backend)
    {
        lock (_gate)
        {
            StopWorkerLocked();
            CancelAllPendingDecisions();

            if (_ownsBackend && _backend is not null)
                _backend.Dispose();

            _backend = backend;
            _ownsBackend = false;
            _lastDllPath = null;
            _cachedDevices = Array.Empty<InterceptionDeviceInfo>();
            _panicTriggered = false;

            if (backend is not null && _subscriptions.Count > 0)
            {
                ResolveAllSubscriptionsLocked();
                EnsureWorkerRunningLocked();
            }
        }

        StateChanged?.Invoke();
    }

    public InterceptionDriverSnapshot GetSnapshot(string? dllPath = null, bool refreshDevices = false)
    {
        var devices = refreshDevices || _cachedDevices.Count == 0
            ? EnumerateDevices(dllPath)
            : _cachedDevices;

        lock (_gate)
        {
            var backend = EnsureBackendLocked(dllPath);
            return new InterceptionDriverSnapshot(
                DriverAvailable: backend.IsAvailable,
                LoadedLibraryPath: backend.LoadedLibraryPath,
                LastError: backend.LastError,
                PanicTriggered: _panicTriggered,
                Devices: devices);
        }
    }

    /// <summary>
    /// Scans slots 1..10 (keyboards) and 11..20 (mice) and computes 1-based instance indices among identical HWIDs.
    /// </summary>
    public IReadOnlyList<InterceptionDeviceInfo> EnumerateDevices(string? dllPath = null)
    {
        lock (_gate)
        {
            var backend = EnsureBackendLocked(dllPath);
            if (!backend.IsAvailable)
            {
                _cachedDevices = Array.Empty<InterceptionDeviceInfo>();
                return _cachedDevices;
            }

            var ctx = _activeContext;
            var createdTemp = false;
            if (ctx == nint.Zero)
            {
                ctx = backend.CreateContext();
                createdTemp = true;
            }

            if (ctx == nint.Zero)
            {
                _cachedDevices = Array.Empty<InterceptionDeviceInfo>();
                return _cachedDevices;
            }

            try
            {
                var list = new List<InterceptionDeviceInfo>();
                var instanceCounts = new Dictionary<(string Kind, string HwId), int>(new DeviceKeyComparer());

                for (var slot = 1; slot <= InterceptionConstants.MaxDevice; slot++)
                {
                    var hwid = backend.GetHardwareId(ctx, slot);
                    if (string.IsNullOrWhiteSpace(hwid))
                        continue;

                    var kind = InterceptionConstants.IsKeyboardSlot(slot) ? "keyboard" : "mouse";
                    var key = (kind, hwid);
                    var nextIndex = instanceCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                    instanceCounts[key] = nextIndex;

                    var (displayName, vendorName, productName) = UsbDeviceNameResolver.Resolve(hwid, kind);
                    list.Add(new InterceptionDeviceInfo(slot, kind, hwid, nextIndex, displayName, vendorName, productName));
                }

                _cachedDevices = list;
                return _cachedDevices;
            }
            finally
            {
                if (createdTemp)
                    backend.DestroyContext(ctx);
            }
        }
    }

    /// <summary>
    /// Resolves a hardware device slot by explicit slot (1..20) or case-insensitive HWID substring + 1-based instance index.
    /// </summary>
    public int ResolveDeviceSlot(string kind, string hardwareIdFilter, int instanceIndex, int explicitSlot)
    {
        var normalizedKind = InterceptionStrokeHelper.NormalizeKind(kind);

        if (explicitSlot > 0)
        {
            if (normalizedKind == "keyboard")
                return InterceptionConstants.IsKeyboardSlot(explicitSlot) ? explicitSlot : 0;
            if (normalizedKind == "mouse")
                return InterceptionConstants.IsMouseSlot(explicitSlot) ? explicitSlot : 0;
            return explicitSlot <= InterceptionConstants.MaxDevice ? explicitSlot : 0;
        }

        var filter = hardwareIdFilter?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
            return 0;

        var devices = _cachedDevices;
        if (devices.Count == 0)
            devices = EnumerateDevices();

        var targetInstance = Math.Max(1, instanceIndex);
        var matchCount = 0;

        foreach (var dev in devices)
        {
            if (!string.IsNullOrEmpty(normalizedKind) &&
                !dev.Kind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (filter == "*" || dev.HardwareId.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                matchCount++;
                if (matchCount == targetInstance)
                    return dev.Slot;
            }
        }

        return 0;
    }

    public IDisposable Subscribe(
        InterceptionSubscriptionConfig config,
        Func<FlowMessage, Task> onStroke,
        out int resolvedSlot,
        out string resolvedHardwareId)
    {
        var subId = Guid.NewGuid().ToString("N")[..12];
        var sub = new ActiveSubscription(subId, config, onStroke);

        lock (_gate)
        {
            if (_subscriptions.Count == 0)
                _panicTriggered = false;

            EnsureBackendLocked(config.DllPath);
            EnumerateDevices(config.DllPath);

            ResolveSubscriptionLocked(sub);
            _subscriptions.Add(sub);
            _filtersDirty = true;

            if (sub.ResolvedSlot > 0 || sub.Config.MatchAllDevices)
                EnsureWorkerRunningLocked();

            resolvedSlot = sub.ResolvedSlot;
            resolvedHardwareId = sub.ResolvedHardwareId;
        }

        StateChanged?.Invoke();
        return new SubscriptionHandle(this, subId);
    }

    /// <summary>
    /// Records a loop-back stroke decision into the active collector for correlationId,
    /// or performs direct hardware injection when the message carries no pending correlationId.
    /// </summary>
    public bool CompleteDecision(FlowMessage returnMessage, string defaultAction = "pass", string? dllPath = null)
    {
        var correlationId = DecisionParser.GetCorrelationId(returnMessage);
        if (!string.IsNullOrEmpty(correlationId))
        {
            if (_pendingDecisions.TryGetValue(correlationId, out var pending))
            {
                var decision = DecisionParser.ParseDecision(returnMessage, defaultAction, allowPayloadFallback: false);
                pending.Add(decision);
                return true;
            }

            if (DecisionParser.TryReadBool(returnMessage.Properties, "directInject") != true)
                return false;
        }

        var directDecision = DecisionParser.ParseDecision(returnMessage, defaultAction, allowPayloadFallback: true);
        if ((directDecision.SendKeys is null || directDecision.SendKeys.Count == 0) &&
            (directDecision.SendMouse is null || directDecision.SendMouse.Count == 0))
        {
            return false;
        }

        return InjectDirect(directDecision, dllPath);
    }

    public void EmergencyReleaseAllFilters()
    {
        lock (_gate)
        {
            _panicTriggered = true;
            _filtersDirty = true;
            if (_backend is not null && _activeContext != nint.Zero)
                ClearAllFiltersLocked(_backend, _activeContext);
        }

        CancelAllPendingDecisions();
        StateChanged?.Invoke();
    }

    public void ResetPanic()
    {
        lock (_gate)
        {
            _panicTriggered = false;
            _filtersDirty = true;
        }

        StateChanged?.Invoke();
    }

    private void Unsubscribe(string subscriptionId)
    {
        lock (_gate)
        {
            var removed = _subscriptions.RemoveAll(s => s.Id == subscriptionId);
            if (removed == 0)
                return;

            _filtersDirty = true;
            if (_subscriptions.Count == 0)
            {
                StopWorkerLocked();
                CancelAllPendingDecisions();
            }
        }

        StateChanged?.Invoke();
    }

    private IInterceptionBackend EnsureBackendLocked(string? dllPath)
    {
        if (_backend is not null)
        {
            if (!_ownsBackend || _backend.IsAvailable)
                return _backend;

            if (string.IsNullOrWhiteSpace(dllPath) ||
                string.Equals(dllPath, _lastDllPath, StringComparison.OrdinalIgnoreCase))
            {
                return _backend;
            }

            _backend.Dispose();
            _backend = null;
        }

        _lastDllPath = dllPath;
        _backend = NativeInterceptionBackend.Load(dllPath);
        _ownsBackend = true;
        return _backend;
    }

    private void ResolveAllSubscriptionsLocked()
    {
        if (_cachedDevices.Count == 0)
            EnumerateDevices();

        foreach (var sub in _subscriptions)
            ResolveSubscriptionLocked(sub);

        _filtersDirty = true;
    }

    private void ResolveSubscriptionLocked(ActiveSubscription sub)
    {
        if (sub.Config.MatchAllDevices)
        {
            sub.ResolvedSlot = 0;
            sub.ResolvedHardwareId = string.Empty;
            return;
        }

        var slot = ResolveDeviceSlot(
            sub.Config.DeviceKind,
            sub.Config.HardwareIdFilter,
            sub.Config.InstanceIndex,
            sub.Config.ExplicitSlot);

        sub.ResolvedSlot = slot;
        sub.ResolvedHardwareId = slot > 0
            ? _cachedDevices.FirstOrDefault(d => d.Slot == slot)?.HardwareId ?? string.Empty
            : string.Empty;
    }

    private sealed class PendingDecision
    {
        private readonly object _sync = new();
        private int _count;
        private bool _anySuppress;
        private bool _anyBlockActions;
        private List<KeyActionStep>? _sendKeys;
        private List<MouseActionStep>? _sendMouse;
        private int _targetSlot;
        private string? _targetHardwareId;

        public void Add(StrokeDecision decision)
        {
            lock (_sync)
            {
                _count++;
                if (decision.Suppress) _anySuppress = true;
                if (decision.BlockActions) _anyBlockActions = true;
                if (decision.SendKeys is { Count: > 0 }) (_sendKeys ??= []).AddRange(decision.SendKeys);
                if (decision.SendMouse is { Count: > 0 }) (_sendMouse ??= []).AddRange(decision.SendMouse);
                if (_targetSlot == 0 && decision.TargetSlot > 0) _targetSlot = decision.TargetSlot;
                if (string.IsNullOrEmpty(_targetHardwareId) && !string.IsNullOrEmpty(decision.TargetHardwareId))
                    _targetHardwareId = decision.TargetHardwareId;
            }
        }

        public StrokeDecision BuildFinal(bool defaultSuppress)
        {
            lock (_sync)
            {
                if (_count == 0)
                    return new StrokeDecision(defaultSuppress);

                if (_anyBlockActions)
                    return new StrokeDecision(Suppress: true, BlockActions: true);

                return new StrokeDecision(
                    _anySuppress,
                    _sendKeys,
                    _sendMouse,
                    _targetSlot,
                    _targetHardwareId,
                    BlockActions: false);
            }
        }
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly InterceptionDriverService _service;
        private readonly string _id;
        private int _disposed;

        public SubscriptionHandle(InterceptionDriverService service, string id)
        {
            _service = service;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _service.Unsubscribe(_id);
        }
    }

    private sealed class DeviceKeyComparer : IEqualityComparer<(string Kind, string HwId)>
    {
        public bool Equals((string Kind, string HwId) x, (string Kind, string HwId) y)
            => string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.HwId, y.HwId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Kind, string HwId) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.HwId));
    }
}
