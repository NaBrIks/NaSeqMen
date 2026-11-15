// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using NaSeqMen.Runtime;

internal static class EventChecks
{
    public static async Task RunAsync()
    {
        var output = new Fake();
        var root = Directory.CreateTempSubdirectory("naseqmen-input-");
        try
        {
            await using var runtime = new GraphRuntime([new TestInput(), output], Path.Combine(root.FullName, "config.json"));
            var config = new Configuration
            {
                DryRun = false,
                Connections = [new() { Id = "local", Module = "windows.input" }],
                Mappings = [new() { Source = "mouse", Control = "key:12", Name = "Mapped E", Policy = "map" }],
                Nodes = [
                    new() { Id = "source", Kind = "source", Module = "windows.input" },
                    new() { Id = "event", Kind = "event", Operation = "sequence", ConnectionId = "local", Settings = new()
                    {
                        ["source"] = "mouse", ["eventName"] = "Win+E",
                        ["steps"] = "key:E0:5B down\nkey:12 down\nkey:12 up\nkey:E0:5B up",
                        ["timeoutMs"] = "1000"
                    } },
                    new() { Id = "held", Kind = "condition", Operation = "modifierHeld",
                        Settings = new() { ["modifier"] = "Ctrl", ["scope"] = "global" } },
                    new() { Id = "action", Kind = "action", Module = "fake", Operation = "do", Settings = new() { ["arg"] = "{control}" } }
                ],
                Edges = [new() { From = "source", To = "event" }, new() { From = "event", To = "held" }, new() { From = "held", To = "action" }]
            };
            await runtime.StartAsync(config);
            var time = DateTimeOffset.UtcNow;
            var modifiers = new ModifierState(ModifierKeys.None, ModifierKeys.LeftWin, ModifierKeys.RightCtrl);
            Task Feed(string code, string phase, string source = "mouse", int delay = 10, long generation = 1, bool held = true)
                => runtime.ProcessAsync(new Signal
                {
                    Module = "windows.input", ConnectionId = "local", Source = source,
                    Control = code, Phase = phase, Time = time = time.AddMilliseconds(delay),
                    ValueType = "number", Value = "1", InputGeneration = generation,
                    Modifiers = held ? modifiers : null
                });
            const string win = "key:E0:5B", e = "key:12";
            await Feed(win, "down");
            await Feed(e, "down");
            await Feed(e, "down");
            await Feed(e, "up", source: "keyboard");
            await Feed(e, "up");
            Check(output.Count == 0, "sequence waits for final release");
            await Feed(win, "up");
            Check(output.Count == 1 && output.Last == "Win+E", "one named event after complete sequence, despite repeat and mapped E");

            await Feed(win, "down");
            await Feed(e, "down", delay: 1500);
            await Feed(e, "up");
            await Feed(win, "up");
            await Feed(win, "down");
            await Feed("device:connection", "removed");
            await Feed(e, "down");
            await Feed(e, "up");
            await Feed(win, "up");
            await Feed(win, "down");
            await Feed(e, "down", generation: 2);
            await Feed(e, "up", generation: 2);
            await Feed(win, "up", generation: 2);
            Check(output.Count == 1, "timeout, removal and input generation invalidate partial matches");

            await Feed(win, "down");
            await Feed(e, "down");
            await Feed(e, "up");
            await Feed(win, "up", held: false);
            Check(output.Count == 1, "unknown modifier state does not pass a held condition");
        }
        finally { File.Delete(Path.Combine(root.FullName, "config.json")); root.Delete(); }
    }

    private static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException(description);
        Console.WriteLine("PASS " + description);
    }

    private sealed class TestInput : NaModule
    {
        public override ModuleDescriptor Descriptor => new("windows.input", "Test input", "1", true, false, [], []);
    }
}
