using NaSeqMen.Core.Messages;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Metadata describing a hardware device slot exposed by the Interception driver.
/// </summary>
public sealed record InterceptionDeviceInfo(
    int Slot,
    string Kind,
    string HardwareId,
    int InstanceIndex,
    string? DisplayName = null,
    string? VendorName = null,
    string? ProductName = null);

/// <summary>
/// Single keyboard stroke step to inject via Interception.
/// </summary>
public sealed record KeyActionStep(
    ushort ScanCode,
    bool IsE0,
    bool IsDown,
    bool IsE1 = false);

/// <summary>
/// Single mouse stroke step to inject via Interception.
/// </summary>
public sealed record MouseActionStep(
    ushort State,
    short Rolling = 0,
    int Dx = 0,
    int Dy = 0,
    ushort Flags = 0);

/// <summary>
/// Decision returned from the flow graph for a captured hardware stroke.
/// </summary>
public sealed record StrokeDecision(
    bool Suppress,
    IReadOnlyList<KeyActionStep>? SendKeys = null,
    IReadOnlyList<MouseActionStep>? SendMouse = null,
    int TargetSlot = 0,
    string? TargetHardwareId = null,
    bool BlockActions = false);

/// <summary>
/// Configuration for a single device subscription managed by <see cref="InterceptionDriverService"/>.
/// </summary>
public sealed record InterceptionSubscriptionConfig
{
    public required string NodeId { get; init; }
    public string OutputPortId { get; init; } = "out";
    public required string DeviceKind { get; init; }
    public string HardwareIdFilter { get; init; } = string.Empty;
    public int InstanceIndex { get; init; } = 1;
    public int ExplicitSlot { get; init; }
    public bool MatchAllDevices { get; init; }
    public bool CaptureMove { get; init; }
    public string DefaultAction { get; init; } = "pass";
    public int DecisionTimeoutMs { get; init; } = 60;
    public string? DllPath { get; init; }
}

/// <summary>
/// Snapshot of the Interception driver runtime status for UI widgets and diagnostics.
/// </summary>
public sealed record InterceptionDriverSnapshot(
    bool DriverAvailable,
    string? LoadedLibraryPath,
    string? LastError,
    bool PanicTriggered,
    IReadOnlyList<InterceptionDeviceInfo> Devices);

internal sealed class ActiveSubscription
{
    public ActiveSubscription(
        string id,
        InterceptionSubscriptionConfig config,
        Func<FlowMessage, Task> onStroke)
    {
        Id = id;
        Config = config;
        OnStroke = onStroke;
    }

    public string Id { get; }
    public InterceptionSubscriptionConfig Config { get; }
    public Func<FlowMessage, Task> OnStroke { get; }
    public int ResolvedSlot { get; set; }
    public string ResolvedHardwareId { get; set; } = string.Empty;
}
