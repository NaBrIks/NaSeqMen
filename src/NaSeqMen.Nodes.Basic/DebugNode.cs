using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;
using NaSeqMen.Core.Telemetry;

namespace NaSeqMen.Nodes.Basic;

public sealed class DebugNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "basic.debug",
        DisplayName = "Debug",
        Category = "Debug",
        Color = "#059669",
        Description = "Inspects messages traveling along wires and streams them to the Debug panel. Also passes the message through.",
        Inputs =
        [
            new PortDefinition
            {
                Id = "in",
                Label = "Message",
                Direction = PortDirection.Input,
                DataType = PortDataType.Any,
                Description = "Any incoming message basket.",
            },
        ],
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Pass-through",
                Direction = PortDirection.Output,
                DataType = PortDataType.Any,
                Description = "Optional pass-through so Debug can be placed inline on a wire.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "active",
                Label = "Active",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Enable or mute output to the Debug sidebar.",
            },
            new ParameterDefinition
            {
                Key = "label",
                Label = "Tag",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "Optional debug tag",
                Inline = true,
            },
            new ParameterDefinition
            {
                Key = "fullBasket",
                Label = "Full Basket",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = false,
                Description = "Show full message basket (payload, topic, properties, meta) or payload only.",
            },
        ],
    };

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override async Task OnInputAsync(string inputPortId, FlowMessage message, CancellationToken cancellationToken)
    {
        if (GetBool("active", true))
        {
            var tag = GetString("label", "");
            var fullBasket = GetBool("fullBasket", true);
            var entry = new DebugEntry
            {
                FlowId = Context.FlowId,
                NodeId = Context.NodeId,
                NodeTitle = string.IsNullOrWhiteSpace(Context.Title) ? Descriptor.DisplayName : Context.Title,
                Label = string.IsNullOrWhiteSpace(tag) ? null : tag,
                SourceNodeId = message.SourceNodeId,
                SourcePortId = message.SourcePortId,
                MessageId = message.Id,
                Topic = message.Topic,
                Payload = message.Payload,
                Properties = fullBasket ? new Dictionary<string, object?>(message.Properties, StringComparer.Ordinal) : null,
                FullBasket = fullBasket,
            };

            Context.PublishDebug(entry);

            var preview = message.PayloadAsString();
            Context.ReportStatus(preview.Length <= 18 ? preview : preview[..18] + "…");
        }

        await EmitAsync("out", message.Clone(), cancellationToken);
    }
}
