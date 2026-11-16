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
                    new() { Id = "side1", Kind = "event", Operation = "match",
                        Settings = new() { ["control"] = "mouse:button4", ["phase"] = "down", ["ignoreRepeat"] = "true" } },
                    new() { Id = "side2", Kind = "event", Operation = "match",
                        Settings = new() { ["control"] = "mouse:button5", ["phase"] = "down" } },
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
                Edges = [
                    new() { From = "source", To = "side1" }, new() { From = "source", To = "side2" },
                    new() { From = "source", To = "event" }, new() { From = "event", To = "held" }, new() { From = "held", To = "action" }
                ]
            };
            await runtime.StartAsync(config);
            var time = DateTimeOffset.UtcNow;
            var modifiers = new ModifierState(ModifierKeys.None, ModifierKeys.LeftWin, ModifierKeys.RightCtrl);
            Task Feed(string code, string phase, string source = "mouse", int delay = 10, long generation = 1, bool held = true, bool repeat = false,
                ModifierState? state = null)
                => runtime.ProcessAsync(new Signal
                {
                    Module = "windows.input", ConnectionId = "local", Source = source,
                    Control = code, Phase = phase, Time = time = time.AddMilliseconds(delay),
                    ValueType = "number", Value = "1", InputGeneration = generation, IsRepeat = repeat,
                    Modifiers = state ?? (held ? modifiers : null)
                });
            using var trace = runtime.Diagnostics.Subscribe();
            void CheckEventArrival(string? match, string description)
            {
                var received = new List<string>();
                var routed = new List<string?>();
                while (trace.Reader.TryRead(out var diagnostic))
                {
                    if (diagnostic.Stage == "received" && diagnostic.NodeId is "side1" or "side2" or "event")
                        received.Add(diagnostic.NodeId);
                    if (diagnostic.Stage == "routed" && diagnostic.NodeId == "source")
                        routed.Add(diagnostic.NextNodeId);
                }
                string[] expected = match is null ? [] : [match];
                Check(received.SequenceEqual(expected) && routed.SequenceEqual(expected), description);
            }
            await Feed("mouse:button4", "down");
            CheckEventArrival("side1", "Side 1 activates only its event node and incoming wire");
            await Feed("mouse:button5", "down");
            CheckEventArrival("side2", "Side 2 activates only its event node and incoming wire");
            await Feed("mouse:button4", "up");
            await Feed("mouse:button4", "down", repeat: true);
            await Feed("mouse:button3", "down");
            CheckEventArrival(null, "Unmatched buttons, phases and ignored repeats do not activate event nodes or wires");

            const string win = "key:E0:5B", e = "key:12";
            await Feed(win, "down");
            await Feed(e, "down");
            await Feed(e, "down");
            await Feed(e, "up", source: "keyboard");
            await Feed(e, "up");
            Check(output.Count == 0, "sequence waits for final release");
            CheckEventArrival(null, "Partial sequence does not activate its event node or wire");
            await Feed(win, "up");
            Check(output.Count == 1 && output.Last == "Win+E", "one named event after complete sequence, despite repeat and mapped E");
            CheckEventArrival("event", "Complete sequence activates its event node and incoming wire once");

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

            config.Nodes.AddRange([
                new() { Id = "chord", Kind = "event", Operation = "chord", ConnectionId = "local",
                    Settings = new() { ["source"] = "mouse", ["control"] = e, ["modifiers"] = "Win" } },
                new() { Id = "fallback", Kind = "event", Operation = "default" },
                new() { Id = "default-action", Kind = "action", Module = "fake", Operation = "do",
                    Settings = new() { ["arg"] = "default:{control}" } }
            ]);
            config.Edges = [
                new() { From = "source", To = "chord" },
                new() { From = "chord", To = "action" },
                new() { From = "chord", To = "side1", Outcome = "miss" },
                new() { From = "side1", To = "action" },
                new() { From = "side1", To = "fallback", Outcome = "miss" },
                new() { From = "fallback", To = "default-action" }
            ];
            Check((await runtime.ApplyAsync(config)).Valid, "ordered Events and Default validate");
            var sourceWin = new ModifierState(ModifierKeys.RightWin, ModifierKeys.RightWin, ModifierKeys.RightWin);
            await Feed(e, "down", state: sourceWin);
            Check(output.Count == 2 && output.Last == "Mapped E", "a chord matches on down and stops the miss chain");
            await Feed(e, "down", state: new(ModifierKeys.None, ModifierKeys.None, ModifierKeys.LeftWin));
            Check(output.Count == 3 && output.Last == "default:Mapped E", "Win held on another device goes to Default once");
            await Feed("mouse:button4", "down");
            Check(output.Count == 4 && output.Last == "mouse:button4", "second Event matches and bypasses Default");
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
