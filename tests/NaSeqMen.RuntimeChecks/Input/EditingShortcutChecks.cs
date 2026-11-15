// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using NaSeqMen.Desktop.Windows;
using NaSeqMen.Runtime;

internal static class EditingShortcutChecks
{
    public static async Task RunAsync()
    {
        var keyboard = new TestKeyboard();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            await using var runtime = new GraphRuntime([new DesktopModule(keyboard)], path);
            var configuration = new Configuration { DryRun = false };
            var bindings = new[]
            {
                (Source: "mouse-interface", Control: "mouse:button4", Key: 'C'),
                (Source: "mouse-interface", Control: "mouse:button5", Key: 'V'),
                (Source: "mouse-keyboard", Control: "key:20", Key: 'X')
            };
            foreach (var (source, control, key) in bindings)
            {
                var input = new NodeDefinition { Id = "in" + key, Kind = "source", Settings = new() { ["source"] = source } };
                var match = new NodeDefinition
                {
                    Id = "match" + key, Kind = "event", Operation = "match",
                    Settings = new() { ["control"] = control, ["phase"] = "down", ["ignoreRepeat"] = "true" }
                };
                var output = new NodeDefinition
                {
                    Id = "out" + key, Kind = "action", Module = "windows.desktop", Operation = "chord",
                    Settings = new() { ["keys"] = "Ctrl+" + key }
                };
                configuration.Nodes.AddRange([input, match, output]);
                configuration.Edges.AddRange([new() { From = input.Id, To = match.Id }, new() { From = match.Id, To = output.Id }]);
            }
            await runtime.StartAsync(configuration);
            foreach (var (source, control, key) in bindings)
            {
                keyboard.Events.Clear();
                var down = new Signal { Source = source, Control = control, Phase = "down" };
                await runtime.ProcessAsync(down);
                await runtime.ProcessAsync(down with { IsRepeat = true });
                await runtime.ProcessAsync(down with { Phase = "up" });
                await runtime.ProcessAsync(down with { Source = "ordinary-keyboard" });
                (ushort, bool)[] expected = [(0xA2, false), (key, false), (key, true), (0xA2, true)];
                Assert(keyboard.Events.SequenceEqual(expected), $"device input reaches the real chord module once for Ctrl+{key}");
            }
            configuration.Nodes.First(node => node.Id == "inC").PreviewOnly = true;
            configuration.Nodes.First(node => node.Id == "outX").PreviewOnly = true;
            configuration.Nodes.Add(new()
            {
                Id = "scriptC", Kind = "script",
                Settings = new()
                {
                    ["code"] = """await Context.EmitAsync("windows.desktop", "", "chord", new() { ["keys"] = "Ctrl+X" }); return Input;"""
                }
            });
            configuration.Edges.Add(new() { From = "matchC", To = "scriptC" });
            Assert((await runtime.ApplyAsync(configuration, persist: false)).Valid, "scoped test profile applies");
            keyboard.Events.Clear();
            using var scopedTrace = runtime.Diagnostics.Subscribe();
            foreach (var (source, control, _) in bindings)
                await runtime.ProcessAsync(new Signal { Source = source, Control = control, Phase = "down" });
            (ushort, bool)[] paste = [(0xA2, false), ('V', false), ('V', true), (0xA2, true)];
            Assert(keyboard.Events.SequenceEqual(paste), "Paste stays live while its neighboring Copy and Cut are in test mode");
            var scopedDiagnostics = new List<Diagnostic>();
            while (scopedTrace.Reader.TryRead(out var step)) scopedDiagnostics.Add(step);
            Assert(scopedDiagnostics.Any(step => step.NodeId == "outC" && step.MessageCode == "runtime.previewSource") &&
                scopedDiagnostics.Any(step => step.NodeId == "scriptC" && step.MessageCode == "runtime.previewSource") &&
                scopedDiagnostics.Any(step => step.NodeId == "outX" && step.MessageCode == "runtime.previewAction"),
                "diagnostics identify source, script behind a test source, and action-level suppression");
            configuration.DryRun = true;
            Assert((await runtime.ApplyAsync(configuration, persist: false)).Valid, "dry-run profile applies");
            keyboard.Events.Clear();
            using var trace = runtime.Diagnostics.Subscribe();
            await runtime.ProcessAsync(new Signal { Source = "mouse-interface", Control = "mouse:button4", Phase = "down" });
            var diagnostics = new List<Diagnostic>();
            while (trace.Reader.TryRead(out var step)) diagnostics.Add(step);
            Assert(keyboard.Events.Count == 0 && diagnostics.Any(step => step.NodeId == "outC" && step.Stage == "suppressed"),
                "dry run reaches Copy and suppresses keyboard injection");
            var change = configuration.Nodes.First(node => node.Id == "matchC");
            change.Kind = "condition";
            change.Operation = "equals";
            Assert(!(await runtime.ApplyAsync(configuration, persist: false)).Valid &&
                runtime.GetConfiguration().Nodes.First(node => node.Id == "matchC").Kind == "event",
                "API prevents an existing event from changing role");
        }
        finally { File.Delete(path); }
    }

    private static void Assert(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class TestKeyboard : IKeyboardOutput
    {
        public List<(ushort, bool)> Events { get; } = [];
        public bool IsDown(KeyboardKey key) => false;
        public void Send(KeyboardKey key, bool up) => Events.Add((key.Code, up));
    }
}
