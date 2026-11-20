using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Windows;

public sealed class WindowTargetNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "win.window-target",
        DisplayName = "Focus Window",
        Category = "Window",
        Color = "#0891b2",
        Description = "Finds a target window by process name or title, optionally brings it to the foreground, and attaches targetWindow metadata to the message basket.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Message",
                Direction = PortDirection.Input,
                DataType = PortDataType.Any,
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "found",
                Label = "Found",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emitted when the target window is found (and focused if enabled).",
            },
            new PortDefinition
            {
                Id = "notFound",
                Label = "Not Found",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emitted if no matching window is open.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "processName",
                Label = "Process",
                Kind = ParameterKind.String,
                DefaultValue = "notepad",
                Placeholder = "e.g. notepad",
                Inline = true,
                Description = "Target process name (with or without .exe).",
            },
            new ParameterDefinition
            {
                Key = "titleContains",
                Label = "Title",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Title substring",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "focus",
                Label = "Bring to Front",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Activate the window before forwarding the message (useful before Keyboard Output).",
            },
            new ParameterDefinition
            {
                Key = "waitAfterFocusMs",
                Label = "Focus Wait (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 40,
                Inline = false,
                Description = "Short pause after focusing so the target window is ready to receive input.",
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var proc = GetString("processName", "");
        var title = GetString("titleContains", "");
        var shouldFocus = GetBool("focus", true);

        var target = WindowHelper.FindWindow(proc, title);
        if (target is null)
        {
            Context.ReportStatus("not found");
            await EmitAsync("notFound", message.Clone(), cancellationToken);
            return;
        }

        if (shouldFocus)
        {
            WindowHelper.FocusWindow(target.Hwnd);
            var waitMs = (int)Math.Clamp(GetNumber("waitAfterFocusMs", 40), 0, 2000);
            if (waitMs > 0)
            {
                await Task.Delay(waitMs, cancellationToken);
            }
        }

        var next = message.Clone();
        next.Properties["targetHwnd"] = target.Hwnd.ToInt64();
        next.Properties["targetWindow"] = new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{target.Hwnd:X}",
            ["process"] = target.ProcessName,
            ["title"] = target.Title,
        };

        Context.ReportStatus(shouldFocus ? $"focused: {target.ProcessName}" : $"found: {target.ProcessName}");
        await EmitAsync("found", next, cancellationToken);
    }
}
