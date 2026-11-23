using System.Text.RegularExpressions;
using NaSeqMen.Core.Messages;

namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Builds stroke baskets and sends keyboard/mouse actions via <see cref="IInterceptionBackend"/>.
/// </summary>
internal static partial class InterceptionStrokeHelper
{
    public static FlowMessage BuildStrokeMessage(
        ActiveSubscription sub,
        string correlationId,
        int device,
        string hardwareId,
        string? deviceName,
        bool isKeyboard,
        in InterceptionStroke stroke)
    {
        var effectiveHwid = !string.IsNullOrEmpty(hardwareId) ? hardwareId : sub.ResolvedHardwareId;
        var effectiveName = !string.IsNullOrEmpty(deviceName) ? deviceName : effectiveHwid;

        if (isKeyboard)
        {
            var rawState = stroke.Key.State;
            var isDown = (rawState & InterceptionConstants.KeyUp) == 0;
            var isE0 = (rawState & InterceptionConstants.KeyE0) != 0;
            var keyName = ScanCodeMap.GetKeyNameFromState(stroke.Key.Code, rawState);

            return new FlowMessage
            {
                Topic = "interception.keyboard",
                Payload = keyName,
                OriginNodeId = sub.Config.NodeId,
                Properties =
                {
                    ["correlationId"] = correlationId,
                    ["deviceSlot"] = device,
                    ["deviceKind"] = "keyboard",
                    ["hardwareId"] = effectiveHwid,
                    ["deviceName"] = effectiveName,
                    ["isDown"] = isDown,
                    ["phase"] = isDown ? "down" : "up",
                    ["key"] = keyName,
                    ["scanCode"] = (int)stroke.Key.Code,
                    ["isE0"] = isE0,
                    ["rawState"] = (int)rawState,
                },
            };
        }

        var (buttonName, phase, mouseDown) = ScanCodeMap.DecodeMouseStroke(in stroke.Mouse);
        return new FlowMessage
        {
            Topic = "interception.mouse",
            Payload = buttonName,
            OriginNodeId = sub.Config.NodeId,
            Properties =
            {
                ["correlationId"] = correlationId,
                ["deviceSlot"] = device,
                ["deviceKind"] = "mouse",
                ["hardwareId"] = effectiveHwid,
                ["deviceName"] = effectiveName,
                ["isDown"] = mouseDown,
                ["phase"] = phase,
                ["button"] = buttonName,
                ["dx"] = stroke.Mouse.X,
                ["dy"] = stroke.Mouse.Y,
                ["wheel"] = (int)stroke.Mouse.Rolling,
                ["rawState"] = (int)stroke.Mouse.State,
            },
        };
    }

    public static void SendDecisionActions(
        IInterceptionBackend backend,
        nint ctx,
        StrokeDecision decision,
        int originDevice,
        string originHardwareId,
        IReadOnlyList<InterceptionDeviceInfo> devices,
        Func<string, string, int, int, int> resolveSlot)
    {
        if (decision.SendKeys is { Count: > 0 })
        {
            var kbSlot = ResolveTargetKeyboardSlot(
                decision.TargetSlot, decision.TargetHardwareId, originDevice, originHardwareId, devices, resolveSlot);

            foreach (var step in decision.SendKeys)
            {
                ushort state = step.IsDown ? InterceptionConstants.KeyDown : InterceptionConstants.KeyUp;
                if (step.IsE0) state |= InterceptionConstants.KeyE0;
                if (step.IsE1) state |= InterceptionConstants.KeyE1;

                var stroke = new InterceptionStroke
                {
                    Key = new InterceptionKeyStroke
                    {
                        Code = step.ScanCode,
                        State = state,
                        Information = 0,
                    },
                };
                backend.Send(ctx, kbSlot, in stroke);
            }
        }

        if (decision.SendMouse is { Count: > 0 })
        {
            var mouseSlot = ResolveTargetMouseSlot(
                decision.TargetSlot, decision.TargetHardwareId, originDevice, devices, resolveSlot);

            foreach (var step in decision.SendMouse)
            {
                var stroke = new InterceptionStroke
                {
                    Mouse = new InterceptionMouseStroke
                    {
                        State = step.State,
                        Flags = step.Flags,
                        Rolling = step.Rolling,
                        X = step.Dx,
                        Y = step.Dy,
                        Information = 0,
                    },
                };
                backend.Send(ctx, mouseSlot, in stroke);
            }
        }
    }

    public static int ResolveTargetKeyboardSlot(
        int targetSlot,
        string? targetHardwareId,
        int originDevice,
        string originHardwareId,
        IReadOnlyList<InterceptionDeviceInfo> devices,
        Func<string, string, int, int, int> resolveSlot)
    {
        if (InterceptionConstants.IsKeyboardSlot(targetSlot))
            return targetSlot;

        if (!string.IsNullOrWhiteSpace(targetHardwareId))
        {
            var resolved = resolveSlot("keyboard", targetHardwareId, 1, 0);
            if (resolved > 0)
                return resolved;
        }

        if (InterceptionConstants.IsKeyboardSlot(originDevice))
            return originDevice;

        // If triggered by a mouse button, prefer a keyboard sharing the same VID/PID prefix if present.
        if (!string.IsNullOrWhiteSpace(originHardwareId))
        {
            var vidPid = ExtractVidPidPrefix(originHardwareId);
            if (!string.IsNullOrEmpty(vidPid))
            {
                var siblingKb = devices.FirstOrDefault(d =>
                    d.Kind == "keyboard" &&
                    d.HardwareId.Contains(vidPid, StringComparison.OrdinalIgnoreCase));
                if (siblingKb is not null)
                    return siblingKb.Slot;
            }
        }

        var firstKb = devices.FirstOrDefault(d => d.Kind == "keyboard");
        return firstKb?.Slot ?? 1;
    }

    public static int ResolveTargetMouseSlot(
        int targetSlot,
        string? targetHardwareId,
        int originDevice,
        IReadOnlyList<InterceptionDeviceInfo> devices,
        Func<string, string, int, int, int> resolveSlot)
    {
        if (InterceptionConstants.IsMouseSlot(targetSlot))
            return targetSlot;

        if (!string.IsNullOrWhiteSpace(targetHardwareId))
        {
            var resolved = resolveSlot("mouse", targetHardwareId, 1, 0);
            if (resolved > 0)
                return resolved;
        }

        if (InterceptionConstants.IsMouseSlot(originDevice))
            return originDevice;

        var firstMouse = devices.FirstOrDefault(d => d.Kind == "mouse");
        return firstMouse?.Slot ?? (InterceptionConstants.MaxKeyboard + 1);
    }

    public static string NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return string.Empty;

        var trimmed = kind.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "kb" or "keyboard" => "keyboard",
            "m" or "mouse" => "mouse",
            _ => trimmed,
        };
    }

    private static string? ExtractVidPidPrefix(string hardwareId)
    {
        var match = VidPidRegex().Match(hardwareId);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}", RegexOptions.CultureInvariant)]
    private static partial Regex VidPidRegex();
}
