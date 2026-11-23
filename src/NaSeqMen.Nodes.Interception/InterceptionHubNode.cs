using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Multi-port hub representing the single Interception driver with dedicated keyboard and mouse channels.
/// </summary>
public sealed class InterceptionHubNode : NodeBase
{
    private static readonly (string PortId, string FilterKey, string Kind)[] Channels =
    [
        ("kb1", "kb1Filter", "keyboard"),
        ("kb2", "kb2Filter", "keyboard"),
        ("kb3", "kb3Filter", "keyboard"),
        ("kb4", "kb4Filter", "keyboard"),
        ("mouse1", "mouse1Filter", "mouse"),
        ("mouse2", "mouse2Filter", "mouse"),
        ("mouse3", "mouse3Filter", "mouse"),
        ("mouse4", "mouse4Filter", "mouse"),
    ];

    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.hub",
        DisplayName = "Interception Hub",
        Category = "Interception",
        Color = "#991b1b",
        Description = "Multi-device Interception driver hub exposing up to 4 keyboard outputs and 4 mouse outputs with a shared loop-back Return / Action input.",
        WidgetKind = "interception-device",
        Inputs =
        [
            new PortDefinition
            {
                Id = "return",
                Label = "Return / Action",
                Direction = PortDirection.Input,
                DataType = PortDataType.Object,
                Description = "Loop-back stroke decision or direct hardware injection port.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "all",
                Label = "All Events",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Universal monitor output receiving events from all active channels (and all connected devices when Monitor All is enabled).",
            },
            new PortDefinition { Id = "kb1", Label = "KB 1", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "kb2", Label = "KB 2", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "kb3", Label = "KB 3", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "kb4", Label = "KB 4", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "mouse1", Label = "Mouse 1", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "mouse2", Label = "Mouse 2", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "mouse3", Label = "Mouse 3", Direction = PortDirection.Output, DataType = PortDataType.Object },
            new PortDefinition { Id = "mouse4", Label = "Mouse 4", Direction = PortDirection.Output, DataType = PortDataType.Object },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "active",
                Label = "Active",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = true,
                Description = "Enable hardware hooks for configured hub channels.",
            },
            new ParameterDefinition
            {
                Key = "monitorAll",
                Label = "Monitor All Devices",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = true,
                Description = "Capture all connected keyboards and mice on the All Events port for device discovery and probing.",
            },
            new ParameterDefinition
            {
                Key = "defaultAction",
                Label = "Default Action",
                Kind = ParameterKind.Select,
                DefaultValue = "pass",
                Inline = true,
                Options =
                [
                    new ParameterOption("pass", "Pass"),
                    new ParameterOption("suppress", "Suppress"),
                ],
            },
            CreateFilterParam("kb1Filter", "KB 1 Filter", "HWID substring, HWID#N, or slot 1-10"),
            CreateFilterParam("kb2Filter", "KB 2 Filter", "HWID substring, HWID#N, or slot 1-10"),
            CreateFilterParam("kb3Filter", "KB 3 Filter", "HWID substring, HWID#N, or slot 1-10"),
            CreateFilterParam("kb4Filter", "KB 4 Filter", "HWID substring, HWID#N, or slot 1-10"),
            CreateFilterParam("mouse1Filter", "Mouse 1 Filter", "HWID substring, HWID#N, or slot 11-20"),
            CreateFilterParam("mouse2Filter", "Mouse 2 Filter", "HWID substring, HWID#N, or slot 11-20"),
            CreateFilterParam("mouse3Filter", "Mouse 3 Filter", "HWID substring, HWID#N, or slot 11-20"),
            CreateFilterParam("mouse4Filter", "Mouse 4 Filter", "HWID substring, HWID#N, or slot 11-20"),
            new ParameterDefinition
            {
                Key = "captureMove",
                Label = "Capture Mouse Move",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "decisionTimeoutMs",
                Label = "Decision Timeout (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 60,
                Inline = false,
            },
            new ParameterDefinition
            {
                Key = "dllPath",
                Label = "DLL Path",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Auto-detect interception.dll",
                Inline = false,
            },
        ],
    };

    private readonly List<IDisposable> _subscriptions = new();
    private readonly Dictionary<string, int> _boundSlots = new(StringComparer.Ordinal);
    private string _defaultAction = "pass";
    private string _dllPath = string.Empty;
    private bool _active;
    private bool _monitorAll;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _active = GetBool("active", false);
        _monitorAll = GetBool("monitorAll", false);
        _defaultAction = GetString("defaultAction", "pass");
        _dllPath = GetString("dllPath", "").Trim();
        var captureMove = GetBool("captureMove", false);
        var timeoutMs = (int)Math.Clamp(GetNumber("decisionTimeoutMs", 60), 5, 2000);

        InterceptionDriverService.Instance.StateChanged += OnServiceStateChanged;
        _boundSlots.Clear();

        if (_active)
        {
            foreach (var (portId, filterKey, kind) in Channels)
            {
                var rawFilter = GetString(filterKey, "").Trim();
                if (string.IsNullOrEmpty(rawFilter))
                    continue;

                var (hwidFilter, instanceIndex, explicitSlot) = ParseChannelFilter(rawFilter);
                var cfg = new InterceptionSubscriptionConfig
                {
                    NodeId = Context.NodeId,
                    OutputPortId = portId,
                    DeviceKind = kind,
                    HardwareIdFilter = hwidFilter,
                    InstanceIndex = instanceIndex,
                    ExplicitSlot = explicitSlot,
                    CaptureMove = captureMove,
                    DefaultAction = _defaultAction,
                    DecisionTimeoutMs = timeoutMs,
                    DllPath = string.IsNullOrEmpty(_dllPath) ? null : _dllPath,
                };

                var capturedPort = portId;
                var mirrorToAll = !_monitorAll;
                var handle = InterceptionDriverService.Instance.Subscribe(
                    cfg,
                    msg => mirrorToAll
                        ? Task.WhenAll(EmitAsync(capturedPort, msg), EmitAsync("all", msg.Clone()))
                        : EmitAsync(capturedPort, msg),
                    out var slot,
                    out _);

                _subscriptions.Add(handle);
                if (slot > 0)
                    _boundSlots[portId] = slot;
            }

            if (_monitorAll)
            {
                var allCfg = new InterceptionSubscriptionConfig
                {
                    NodeId = Context.NodeId,
                    OutputPortId = "all",
                    DeviceKind = "any",
                    MatchAllDevices = true,
                    CaptureMove = captureMove,
                    DefaultAction = _defaultAction,
                    DecisionTimeoutMs = timeoutMs,
                    DllPath = string.IsNullOrEmpty(_dllPath) ? null : _dllPath,
                };

                var allHandle = InterceptionDriverService.Instance.Subscribe(
                    allCfg,
                    msg => EmitAsync("all", msg),
                    out _,
                    out _);

                _subscriptions.Add(allHandle);
            }
        }

        PublishState(refreshDevices: true);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CleanupSubscriptions();
        return Task.CompletedTask;
    }

    public override Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (inputPortId == "return")
        {
            InterceptionDriverService.Instance.CompleteDecision(
                message,
                _defaultAction,
                string.IsNullOrEmpty(_dllPath) ? null : _dllPath);
        }

        return Task.CompletedTask;
    }

    public override Task OnInteractAsync(string action, JsonElement? data, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "refreshDevices":
                PublishState(refreshDevices: true);
                break;
            case "panicRelease":
                InterceptionDriverService.Instance.EmergencyReleaseAllFilters();
                PublishState(refreshDevices: false);
                break;
            case "resetPanic":
                InterceptionDriverService.Instance.ResetPanic();
                PublishState(refreshDevices: false);
                break;
        }

        return Task.CompletedTask;
    }

    private void OnServiceStateChanged() => PublishState(refreshDevices: false);

    private void PublishState(bool refreshDevices)
    {
        var snapshot = InterceptionDriverService.Instance.GetSnapshot(
            string.IsNullOrEmpty(_dllPath) ? null : _dllPath,
            refreshDevices);

        var status = snapshot.PanicTriggered
            ? "PANIC: released"
            : !_active
                ? "inactive"
                : !snapshot.DriverAvailable
                    ? "driver unavailable"
                    : _monitorAll
                        ? $"hooked: ALL ({_boundSlots.Count} ch)"
                        : $"hooked: {_boundSlots.Count} ch";

        Context.ReportStatus(status);
        PublishWidgetState(new
        {
            mode = "hub",
            active = _active,
            monitorAll = _monitorAll,
            driverAvailable = snapshot.DriverAvailable,
            loadedLibraryPath = snapshot.LoadedLibraryPath,
            lastError = snapshot.LastError,
            panicTriggered = snapshot.PanicTriggered,
            boundSlots = _boundSlots,
            devices = snapshot.Devices,
        });
    }

    private void CleanupSubscriptions()
    {
        InterceptionDriverService.Instance.StateChanged -= OnServiceStateChanged;
        foreach (var sub in _subscriptions)
            sub.Dispose();
        _subscriptions.Clear();
        _boundSlots.Clear();
    }

    public override ValueTask DisposeAsync()
    {
        CleanupSubscriptions();
        return ValueTask.CompletedTask;
    }

    private static (string HwidFilter, int InstanceIndex, int ExplicitSlot) ParseChannelFilter(string raw)
    {
        if (int.TryParse(raw, out var slot) && slot is >= 1 and <= InterceptionConstants.MaxDevice)
            return (string.Empty, 1, slot);

        var hashIdx = raw.LastIndexOf('#');
        if (hashIdx > 0 && hashIdx < raw.Length - 1 &&
            int.TryParse(raw[(hashIdx + 1)..], out var instance) && instance >= 1)
        {
            return (raw[..hashIdx].Trim(), instance, 0);
        }

        return (raw, 1, 0);
    }

    private static ParameterDefinition CreateFilterParam(string key, string label, string placeholder)
        => new()
        {
            Key = key,
            Label = label,
            Kind = ParameterKind.String,
            DefaultValue = "",
            Placeholder = placeholder,
            Inline = false,
        };
}
