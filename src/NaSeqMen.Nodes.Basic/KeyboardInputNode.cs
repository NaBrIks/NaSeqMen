using NaSeqMen.Core.Messages;
using NaSeqMen.Core.Nodes;

namespace NaSeqMen.Nodes.Basic;

public sealed class KeyboardInputNode : NodeBase
{
    public static readonly NodeTypeDescriptor TypeDescriptor = new()
    {
        TypeId = "basic.keyboard-input",
        DisplayName = "Keyboard Input",
        Category = "Input",
        Color = "#2563eb",
        Description = "Listens to passive physical keyboard events (Windows Raw Input) or emits test text on manual trigger.",
        SupportsManualTrigger = true,
        Inputs = Array.Empty<PortDefinition>(),
        Outputs =
        [
            new PortDefinition
            {
                Id = "out",
                Label = "Key / Text",
                Direction = PortDirection.Output,
                DataType = PortDataType.String,
                Description = "Emits the pressed character or key name inside a message basket.",
            },
        ],
        Parameters =
        [
            new ParameterDefinition
            {
                Key = "captureGlobal",
                Label = "Capture Keyboard",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Observe physical Windows keyboard events passively without blocking input.",
            },
            new ParameterDefinition
            {
                Key = "printableOnly",
                Label = "Printable Only",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = true,
                Description = "Emit only printable characters (letters, digits, punctuation). Uncheck to include Shift, Enter, F-keys, etc.",
            },
            new ParameterDefinition
            {
                Key = "onlyKeyDown",
                Label = "Key Down Only",
                Kind = ParameterKind.Boolean,
                DefaultValue = true,
                Inline = false,
                Description = "Ignore key release events.",
            },
            new ParameterDefinition
            {
                Key = "filterKey",
                Label = "Key Filter",
                Kind = ParameterKind.String,
                DefaultValue = "",
                Placeholder = "All keys (or e.g. A, Space, F9)",
                Inline = true,
                Description = "Optional comma-separated key names to match; empty captures all keys.",
            },
            new ParameterDefinition
            {
                Key = "testText",
                Label = "Manual Payload",
                Kind = ParameterKind.String,
                DefaultValue = "hello world",
                Placeholder = "Text sent on trigger button",
                Inline = true,
                Description = "Payload emitted when clicking the manual trigger button on this node.",
            },
        ],
    };

    private IDisposable? _subscription;
    private bool _onlyKeyDown;
    private bool _printableOnly;
    private HashSet<string>? _filterKeys;

    public override NodeTypeDescriptor Descriptor => TypeDescriptor;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _onlyKeyDown = GetBool("onlyKeyDown", true);
        _printableOnly = GetBool("printableOnly", true);

        var filterRaw = GetString("filterKey", "").Trim();
        if (!string.IsNullOrEmpty(filterRaw))
        {
            _filterKeys = filterRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _filterKeys = null;
        }

        if (GetBool("captureGlobal", true))
        {
            _subscription = RawKeyboardListener.Instance.Subscribe(OnRawKey);
            Context.ReportStatus("listening");
        }
        else
        {
            Context.ReportStatus("manual");
        }

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public override async Task OnManualTriggerAsync(FlowMessage? message, CancellationToken cancellationToken)
    {
        var text = message is not null ? message.PayloadAsString() : GetString("testText", "hello world");
        var basket = new FlowMessage
        {
            Topic = "keyboard.manual",
            Payload = text,
            OriginNodeId = Context.NodeId,
            Properties =
            {
                ["source"] = "manual-trigger",
                ["nodeTitle"] = Context.Title,
            },
        };

        Context.ReportStatus($"sent: {Truncate(text, 16)}");
        await EmitAsync("out", basket, cancellationToken);
    }

    private void OnRawKey(RawKeyEvent evt)
    {
        if (_onlyKeyDown && !evt.IsKeyDown)
            return;

        if (_printableOnly && string.IsNullOrEmpty(evt.Character))
            return;

        var payloadText = evt.Character ?? evt.KeyName;

        if (_filterKeys is not null &&
            !_filterKeys.Contains(evt.KeyName) &&
            !_filterKeys.Contains(payloadText))
        {
            return;
        }

        var basket = new FlowMessage
        {
            Topic = "keyboard.raw",
            Payload = payloadText,
            OriginNodeId = Context.NodeId,
            Properties =
            {
                ["source"] = "raw-input",
                ["key"] = evt.KeyName,
                ["vkCode"] = evt.VirtualKey,
                ["printable"] = !string.IsNullOrEmpty(evt.Character),
                ["phase"] = evt.IsKeyDown ? "down" : "up",
                ["shift"] = evt.Shift,
                ["ctrl"] = evt.Ctrl,
                ["alt"] = evt.Alt,
            },
        };

        Context.ReportStatus($"key: {payloadText}");
        _ = EmitAsync("out", basket);
    }

    public override ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
