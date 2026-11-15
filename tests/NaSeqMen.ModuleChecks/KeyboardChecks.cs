// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using NaSeqMen.Desktop.Windows;

internal static class KeyboardChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        static ActionRequest Key(string connection, string source, string key, string phase) =>
            new(connection, "key", new() { ["key"] = key, ["phase"] = phase },
                new Signal { Source = source, Control = "trigger" });
        var ctrl = new KeyboardKey(0xA2);
        var c = new KeyboardKey('C');
        var x = new KeyboardKey('X');
        var output = new FakeKeyboard();
        await using (var module = new DesktopModule(output))
        {
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            check(output.Sent.Count == 1, "repeated down has single ownership");
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "up"));
            check(output.Sent.SequenceEqual(new[] { (ctrl, false), (ctrl, true) }), "one up releases repeated own down");
            output.Sent.Clear();
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            await module.ExecuteAsync(Key("one", "button-b", "Ctrl", "down"));
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "up"));
            check(output.Down.Contains(ctrl) && output.Sent.Count == 1, "other trigger retains shared Ctrl");
            await module.ExecuteAsync(Key("one", "button-b", "Ctrl", "up"));
            check(!output.Down.Contains(ctrl) && output.Sent.Count == 2, "last trigger releases shared Ctrl");
            output.Sent.Clear();
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            await module.ExecuteAsync(Key("two", "button-a", "Ctrl", "down"));
            await module.DisconnectAsync("one");
            check(output.Down.Contains(ctrl), "disconnect preserves another connection's hold");
            await module.DisconnectAsync("two");
            check(!output.Down.Contains(ctrl), "disconnect releases final owned hold");
            output.Sent.Clear();
            output.Down.Add(ctrl); // A user or another application already holds it.
            await module.ExecuteAsync(new("one", "chord", new() { ["keys"] = "Ctrl+C" }, new()));
            check(output.Sent.SequenceEqual(new[] { (c, false), (c, true) }) && output.Down.Contains(ctrl),
                "chord preserves preexisting foreign modifier");
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "up"));
            check(output.Sent.Count == 2 && output.Down.Contains(ctrl), "unmatched up never releases foreign key");
            output.Down.Remove(ctrl);
            output.Sent.Clear();
            output.FailOnce = (c, false);
            var failed = await module.ExecuteAsync(new("one", "chord", new() { ["keys"] = "Ctrl+C" }, new()));
            check(!failed.Success && !output.Down.Contains(ctrl), "failed chord releases earlier acquired modifier");
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            await module.ExecuteAsync(Key("one", "button-a", "X", "down"));
            output.FailOnce = (x, true);
            var cleanupFailed = false;
            try { await module.DisconnectAsync("one"); }
            catch (AggregateException) { cleanupFailed = true; }
            check(cleanupFailed && !output.Down.Contains(ctrl) && output.Down.Contains(x), "cleanup attempts every key despite one release failure");
            await module.DisconnectAsync("one");
            check(!output.Down.Contains(x), "failed release ownership retained for retry");
            using var cancel = new CancellationTokenSource();
            output.OnSend = (key, up) => { if (key == ctrl && !up) cancel.Cancel(); };
            var cancelled = false;
            try { await module.ExecuteAsync(new("one", "chord", new() { ["keys"] = "Ctrl+C" }, new()), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !output.Down.Contains(ctrl), "cancelled chord releases acquired modifier");
            output.OnSend = null;
            await module.ExecuteAsync(Key("one", "button-a", "Ctrl", "down"));
            await module.ExecuteAsync(Key("one", "button-a", "X", "down"));
        }
        check(output.Down.Count == 0, "Dispose releases all module-owned holds");
    }

    private sealed class FakeKeyboard : IKeyboardOutput
    {
        public HashSet<KeyboardKey> Down { get; } = [];
        public List<(KeyboardKey Key, bool Up)> Sent { get; } = [];
        public (KeyboardKey Key, bool Up)? FailOnce { get; set; }
        public Action<KeyboardKey, bool>? OnSend { get; set; }
        public bool IsDown(KeyboardKey key) => Down.Contains(key);
        public void Send(KeyboardKey key, bool up)
        {
            if (FailOnce is { } failure && failure == (key, up))
            { FailOnce = null; throw new IOException("Test sender rejection"); }
            Sent.Add((key, up));
            if (up) Down.Remove(key); else Down.Add(key);
            OnSend?.Invoke(key, up);
        }
    }
}
