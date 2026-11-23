using System.Text.Json;
using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Universal Interception single-device node with loop-back decision return port
/// and direct hardware injection support.
/// </summary>
public sealed class InterceptionDeviceNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "interception.device",
        DisplayName = "Interception Device",
        Category = "Interception",
        Color = "#dc2626",
        Description = "Captures hardware keyboard or mouse strokes from a specific Interception device slot (matched by Hardware ID or explicit slot) and accepts suppress/replace decisions or direct injections on the Return / Action input.",
        SupportsManualTrigger = true,
        WidgetKind = "interception-device",
        Inputs =
        [
            new PortDefinition
            {
                Id = "return",
                Label = "Return / Action",
                Direction = PortDirection.Input,
                DataType = PortDataType.Object,
                Description = "Loop-back stroke decision (suppress/pass + sendKeys/sendMouse) or direct hardware injection when no correlationId is pending.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Stroke Event",
                Direction = PortDirection.Output,
                DataType = PortDataType.Object,
                Description = "Emits captured hardware stroke basket containing correlationId, device metadata, key/button, and phase.",
            },
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
                Description = "Enable hardware hook for the matched device. Disabled by default for safety.",
            },
            new ParameterDefinition
            {
                Key = "deviceKind",
                Label = "Device Kind",
                Kind = ParameterKind.Select,
                DefaultValue = "keyboard",
                Inline = true,
                Options =
                [
                    new ParameterOption("keyboard", "Keyboard"),
                    new ParameterOption("mouse", "Mouse"),
                ],
            },
            new ParameterDefinition
            {
                Key = "hardwareIdFilter",
                Label = "Hardware ID",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "HWID substring (e.g. VID_...&MI_00)",
                Inline = true,
                Description = "Case-insensitive Hardware ID substring persistent across USB port changes (or * for first device of kind).",
            },
            new ParameterDefinition
            {
                Key = "instanceIndex",
                Label = "Instance Index",
                Kind = ParameterKind.Number,
                DefaultValue = 1,
                Inline = false,
                Description = "1-based index when multiple devices share the same Hardware ID filter.",
            },
            new ParameterDefinition
            {
                Key = "explicitSlot",
                Label = "Explicit Slot",
                Kind = ParameterKind.Number,
                DefaultValue = 0,
                Inline = false,
                Description = "0 = match by Hardware ID; 1-10 KB or 11-20 Mouse",
            },
            new ParameterDefinition
            {
                Key = "captureMove",
                Label = "Capture Mouse Move",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = false,
                Description = "Include relative mouse movement events (only applies to mouse devices).",
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
                Description = "Fallback decision if the loop-back times out or omits an explicit suppress flag.",
            },
            new ParameterDefinition
            {
                Key = "decisionTimeoutMs",
                Label = "Decision Timeout (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 60,
                Inline = false,
                Description = "Maximum time to wait for a loop-back decision before applying Default Action.",
            },
            new ParameterDefinition
            {
                Key = "dllPath",
                Label = "DLL Path",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Auto-detect interception.dll",
                Inline = false,
                Description = "Optional explicit path to interception.dll.",
            },
            new ParameterDefinition
            {
                Key = "testKey",
                Label = "Test Trigger Key",
                Kind = ParameterKind.Key,
                DefaultValue = "D",
                Inline = false,
                Description = "Key or mouse button name emitted when manually triggering this node.",
            },
        ],
    };

    private IDisposable? _subscription;
    private string _defaultAction = "pass";
    private string _dllPath = string.Empty;
    private int _resolvedSlot;
    private string _resolvedHardwareId = string.Empty;
    private bool _active;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _active = GetBool("active", false);
        _defaultAction = GetString("defaultAction", "pass");
        _dllPath = GetString("dllPath", "").Trim();
        var deviceKind = GetString("deviceKind", "keyboard");
        var hwidFilter = GetString("hardwareIdFilter", "").Trim();
        var instanceIndex = Math.Max(1, (int)GetNumber("instanceIndex", 1));
        var explicitSlot = (int)GetNumber("explicitSlot", 0);
        var captureMove = GetBool("captureMove", false);
        var timeoutMs = (int)Math.Clamp(GetNumber("decisionTimeoutMs", 60), 5, 2000);

        InterceptionDriverService.Instance.StateChanged += OnServiceStateChanged;

        if (_active)
        {
            var cfg = new InterceptionSubscriptionConfig
            {
                NodeId = Context.NodeId,
                OutputPortId = "out",
                DeviceKind = deviceKind,
                HardwareIdFilter = hwidFilter,
                InstanceIndex = instanceIndex,
                ExplicitSlot = explicitSlot,
                CaptureMove = captureMove,
                DefaultAction = _defaultAction,
                DecisionTimeoutMs = timeoutMs,
                DllPath = string.IsNullOrEmpty(_dllPath) ? null : _dllPath,
            };

            _subscription = InterceptionDriverService.Instance.Subscribe(
                cfg,
                OnDriverStrokeAsync,
                out _resolvedSlot,
                out _resolvedHardwareId);
        }
        else
        {
            _resolvedSlot = 0;
            _resolvedHardwareId = string.Empty;
        }

        PublishState(refreshDevices: true);
        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        InterceptionDriverService.Instance.StateChanged -= OnServiceStateChanged;
        _subscription?.Dispose();
        _subscription = null;
        _resolvedSlot = 0;
        _resolvedHardwareId = string.Empty;
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

    public override async Task OnManualTriggerAsync(FlowMessage? message, CancellationToken cancellationToken)
    {
        var deviceKind = InterceptionStrokeHelper.NormalizeKind(GetString("deviceKind", "keyboard"));
        var keyOrButton = message is not null && !string.IsNullOrWhiteSpace(message.PayloadAsString())
            ? message.PayloadAsString().Trim()
            : GetString("testKey", "D").Trim();

        var basket = new FlowMessage
        {
            Topic = deviceKind == "mouse" ? "interception.mouse" : "interception.keyboard",
            Payload = keyOrButton,
            OriginNodeId = Context.NodeId,
            Properties =
            {
                ["source"] = "manual-trigger",
                ["deviceSlot"] = _resolvedSlot,
                ["deviceKind"] = deviceKind,
                ["hardwareId"] = _resolvedHardwareId,
                ["isDown"] = true,
                ["phase"] = "down",
            },
        };

        if (deviceKind == "mouse")
        {
            basket.Properties["button"] = keyOrButton;
            basket.Properties["dx"] = 0;
            basket.Properties["dy"] = 0;
            basket.Properties["wheel"] = 0;
            basket.Properties["rawState"] = 0;
        }
        else
        {
            ScanCodeMap.TryGetScanCode(keyOrButton, out var sc, out var isE0);
            basket.Properties["key"] = keyOrButton;
            basket.Properties["scanCode"] = (int)sc;
            basket.Properties["isE0"] = isE0;
            basket.Properties["rawState"] = isE0 ? (int)InterceptionConstants.KeyE0 : 0;
        }

        Context.ReportStatus($"test: {keyOrButton}");
        await EmitAsync("out", basket, cancellationToken);
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

    private Task OnDriverStrokeAsync(FlowMessage basket)
    {
        Context.ReportStatus($"{basket.PayloadAsString()} ({basket.Properties["phase"]})");
        return EmitAsync("out", basket);
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
                    : _resolvedSlot > 0
                        ? $"hooked slot {_resolvedSlot}"
                        : "no device match";

        Context.ReportStatus(status);
        PublishWidgetState(new
        {
            mode = "device",
            active = _active,
            driverAvailable = snapshot.DriverAvailable,
            loadedLibraryPath = snapshot.LoadedLibraryPath,
            lastError = snapshot.LastError,
            panicTriggered = snapshot.PanicTriggered,
            resolvedSlot = _resolvedSlot,
            resolvedHardwareId = _resolvedHardwareId,
            devices = snapshot.Devices,
        });
    }

    public override ValueTask DisposeAsync()
    {
        InterceptionDriverService.Instance.StateChanged -= OnServiceStateChanged;
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }
}
