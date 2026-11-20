using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Windows;

public sealed class WindowPostTextNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "win.window-post-text",
        DisplayName = "Window PostText",
        Category = "Window",
        Color = "#7c3aed",
        Description = "Sends text in the background via Win32 PostMessage(WM_CHAR) without switching focus. Works best with classic Win32 / Edit controls; modern apps (Electron, games) usually need Focus Window + Keyboard Output.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Text",
                Direction = PortDirection.Input,
                DataType = PortDataType.String,
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "sent",
                Label = "Sent",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
            },
            new PortDefinition
            {
                Id = "notFound",
                Label = "Not Found",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "active",
                Label = "Active (Post)",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Uncheck to mute background posting without disconnecting the wire.",
            },
            new ParameterDefinition
            {
                Key = "processName",
                Label = "Process",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "e.g. notepad (or from Focus Window)",
                Inline = true,
                Description = "Leave empty to reuse targetHwnd placed in the basket by Focus Window.",
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
                Key = "childClass",
                Label = "Child Class",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Auto (Edit/RichEdit) or class",
                Inline = false,
                Description = "Optional Win32 child control class name to receive WM_CHAR.",
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Context.ReportStatus(GetBool("active", true) ? "ready" : "muted");
        return Task.CompletedTask;
    }

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (!GetBool("active", true))
        {
            Context.ReportStatus("muted");
            return;
        }

        var text = message.PayloadAsString();
        if (string.IsNullOrEmpty(text))
            return;

        nint hwnd = ResolveHwnd(message);
        if (hwnd == 0)
        {
            Context.ReportStatus("not found");
            await EmitAsync("notFound", message.Clone(), cancellationToken);
            return;
        }

        var childClass = GetString("childClass", "");
        var ok = WindowHelper.PostText(hwnd, text, childClass);
        if (!ok)
        {
            Context.ReportStatus("post failed");
            await EmitAsync("notFound", message.Clone(), cancellationToken);
            return;
        }

        var preview = text.Length <= 14 ? text : text[..14] + "…";
        Context.ReportStatus($"posted: {preview}");

        var next = message.Clone();
        next.Properties["postedHwnd"] = $"0x{hwnd:X}";
        await EmitAsync("sent", next, cancellationToken);
    }

    private nint ResolveHwnd(FlowMessage message)
    {
        var proc = GetString("processName", "");
        var title = GetString("titleContains", "");

        if (!string.IsNullOrWhiteSpace(proc) || !string.IsNullOrWhiteSpace(title))
        {
            return WindowHelper.FindWindow(proc, title)?.Hwnd ?? 0;
        }

        if (message.Properties.TryGetValue("targetHwnd", out var raw) && raw is long handleVal && handleVal != 0)
        {
            return (nint)handleVal;
        }

        return 0;
    }
}
