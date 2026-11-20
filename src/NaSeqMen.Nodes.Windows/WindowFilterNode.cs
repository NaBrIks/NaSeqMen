using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Windows;

public sealed class WindowFilterNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "win.window-filter",
        DisplayName = "Window Filter",
        Category = "Window",
        Color = "#0284c7",
        Description = "Routes incoming messages based on whether the currently active Windows foreground window matches the process name or title.",
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
                Id = "match",
                Label = "Match",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emitted when the active window matches.",
            },
            new PortDefinition
            {
                Id = "noMatch",
                Label = "No Match",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Emitted when the active window does not match.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "processName",
                Label = "Process",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "e.g. notepad",
                Inline = true,
                Description = "Process name to match (with or without .exe).",
            },
            new ParameterDefinition
            {
                Key = "titleContains",
                Label = "Title",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Title substring",
                Inline = true,
                Description = "Optional substring to match in the window title.",
            },
            new ParameterDefinition
            {
                Key = "invert",
                Label = "Invert Match",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = false,
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var fg = WindowHelper.GetForegroundWindowInfo();
        var proc = GetString("processName", "");
        var title = GetString("titleContains", "");
        var invert = GetBool("invert", false);

        bool matched = fg is not null && WindowHelper.Matches(fg, proc, title);
        if (invert)
            matched = !matched;

        var next = message.Clone();
        if (fg is not null)
        {
            next.Properties["activeWindow"] = new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{fg.Hwnd:X}",
                ["process"] = fg.ProcessName,
                ["title"] = fg.Title,
            };
        }

        var fgLabel = fg is not null ? fg.ProcessName : "none";
        Context.ReportStatus(matched ? $"match: {fgLabel}" : $"skip: {fgLabel}");
        await EmitAsync(matched ? "match" : "noMatch", next, cancellationToken);
    }
}
