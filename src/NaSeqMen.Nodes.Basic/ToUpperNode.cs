using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Basic;

public sealed class ToUpperNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "basic.to-upper",
        DisplayName = "CAPS (ToUpper)",
        Category = "Transform",
        Color = "#d97706",
        Description = "Converts incoming message string payload to UPPERCASE while preserving the rest of the data basket.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Input",
                Direction = PortDirection.Input,
                DataType = PortDataType.String,
                Description = "Incoming message with text payload.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "UPPER",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
                Description = "Message with uppercased string payload.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "trim",
                Label = "Trim Whitespace",
                Kind = ParameterKind.Boolean,
                DefaultValue = false,
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "prefix",
                Label = "Prefix",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional prefix",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "suffix",
                Label = "Suffix",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional suffix",
                Inline = false,
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        var text = message.PayloadAsString();
        if (GetBool("trim", false))
            text = text.Trim();

        var transformed = GetString("prefix", "") + text.ToUpperInvariant() + GetString("suffix", "");
        var next = message.WithPayload(transformed);
        next.Properties["transformedBy"] = Descriptor.TypeId;

        Context.ReportStatus(transformed.Length <= 16 ? transformed : transformed[..16] + "…");
        await EmitAsync("out", next, cancellationToken);
    }
}
