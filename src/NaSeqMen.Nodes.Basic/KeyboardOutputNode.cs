using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Basic;

public sealed class KeyboardOutputNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "basic.keyboard-output",
        DisplayName = "Keyboard Output",
        Category = "Output",
        Color = "#9333ea",
        Description = "Types incoming text into the currently focused Windows window via SendInput (Unicode). Tagged so it will not loop back into Keyboard Input.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Text",
                Direction = PortDirection.Input,
                DataType = PortDataType.String,
                Description = "Text payload to type into the active window.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Sent",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
                Description = "Optional pass-through emitted after sending.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "active",
                Label = "Active (Send)",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Uncheck to mute output safely without disconnecting the wire.",
            },
            new ParameterDefinition
            {
                Key = "delayMs",
                Label = "Delay (ms)",
                Kind = ParameterKind.Number,
                DefaultValue = 0,
                Inline = false,
                Description = "Optional delay in milliseconds before typing.",
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

        var delayMs = (int)Math.Clamp(GetNumber("delayMs", 0), 0, 5000);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        KeyboardSender.SendText(text);

        var preview = text.Length <= 16 ? text : text[..16] + "…";
        Context.ReportStatus($"typed: {preview}");

        var next = message.Clone();
        next.Properties["sentBy"] = Descriptor.TypeId;
        await EmitAsync("out", next, cancellationToken);
    }
}
