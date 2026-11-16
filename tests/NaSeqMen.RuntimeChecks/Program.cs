// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using NaSeqMen.Runtime;

var module = new Fake();
await using var runtime = new GraphRuntime([module], Path.Combine(AppContext.BaseDirectory, "config.json"));
var config = new Configuration
{
    Nodes = [
        new() { Id = "in", Kind = "source", Module = "test" },
        new() { Id = "condition", Kind = "condition", Operation = "equals", Settings = new() { ["field"] = "phase", ["value"] = "down" } },
        new() { Id = "script", Kind = "script", Settings = new() { ["code"] = "Context.Trace(\"script\"); var result = await Context.EmitAsync(\"fake\", \"\", \"do\", new() { [\"arg\"] = Input.Value }); Context.Trace(result.Suppressed ? \"explicit-suppressed\" : \"live\"); return Input with { Value = Input.Value.ToUpperInvariant() };" } },
        new() { Id = "out", Kind = "action", Module = "fake", Operation = "do", Settings = new() { ["arg"] = "{value}" } }
    ],
    Edges = [new() { From = "in", To = "condition" }, new() { From = "condition", To = "script" }, new() { From = "script", To = "out" }]
};
await runtime.StartAsync(config);
using var trace = runtime.Diagnostics.Subscribe();
await runtime.ProcessAsync(new Signal { Module = "test", Phase = "down", Value = "hello" });
var diagnostics = new List<Diagnostic>();
while (trace.Reader.TryRead(out var item)) diagnostics.Add(item);
Assert(module.Count == 0, "dryrun host and node suppress external effects");
Assert(diagnostics.Count(d => d.Stage == "suppressed") == 2, "script + node both show suppression");
Assert(diagnostics.Any(d => d.Stage == "script" && d.Signal?.Value == "HELLO"), "real Roslyn modifies payload");
Assert(diagnostics.Any(d => d.Message == "explicit-suppressed"), "C# can distinguish suppressed from error");
Assert(diagnostics.Any(d => d.Stage == "routed" && d.NodeId == "condition" && d.NextNodeId == "script"), "exact edge diagnostic identifies both nodes");
await runtime.ProcessAsync(new Signal { Module = "test", Phase = "up", Value = "hello" });
diagnostics.Clear();
while (trace.Reader.TryRead(out var item)) diagnostics.Add(item);
Assert(diagnostics.Any(d => d.Stage == "blocked") && !diagnostics.Any(d => d.Stage == "intended"), "condition blocks branch");
Assert(!diagnostics.Any(d => d.Stage == "routed" && d.NodeId == "condition"), "blocked condition does not route outgoing wire");
config.DryRun = false;
Assert((await runtime.ApplyAsync(config)).Valid, "apply live fake configuration");
await runtime.ProcessAsync(new Signal { Module = "test", Phase = "down", Value = "hello" });
Assert(module.Count == 2 && module.Last == "HELLO", "fake receives host and node calls in order");
var discoveryOwner = Guid.NewGuid();
var discoveryInput = new Signal { Module = "test", Phase = "down", Value = "discovery test" };
var discovery = new DiscoveryCaptureRequest(discoveryOwner, ["some keyboard"]);
await runtime.SetDiscoveryAsync(discovery, CancellationToken.None);
Assert(module.CaptureInterfaces.SequenceEqual(["some keyboard"]), "normal discovery arms the module capture");
while (trace.Reader.TryRead(out _)) { }
Assert(runtime.TryPublish(discoveryInput), "discovery accepts input for diagnostics");
await runtime.ProcessAsync(discoveryInput);
await runtime.SetDiscoveryAsync(new(Guid.NewGuid(), []), CancellationToken.None);
await runtime.ProcessAsync(discoveryInput);
Assert(module.Count == 2 && trace.Reader.TryRead(out var captured) &&
    captured.Stage == "input" && !trace.Reader.TryRead(out _),
    "discovery blocks all graph sources and ignores another owner's release");
await runtime.SetDiscoveryAsync(discovery with { AllowEditing = true }, CancellationToken.None);
await runtime.ProcessAsync(discoveryInput);
Assert(module.CaptureInterfaces.Length == 0 && module.Count == 2,
    "editing releases physical capture while all graph sources remain paused");
await runtime.SetDiscoveryAsync(new(discoveryOwner, []), CancellationToken.None);
await runtime.ProcessAsync(discoveryInput);
Assert(module.Count == 4, "discovery owner release restores routing");
var broken = runtime.GetConfiguration();
broken.Nodes[2].Settings["code"] = "this is not C#";
Assert(!(await runtime.ApplyAsync(broken)).Valid, "bad C# rejected");
Assert(runtime.GetConfiguration().Nodes[2].Settings["code"] == config.Nodes[2].Settings["code"], "bad C# leaves working configuration");
broken = runtime.GetConfiguration();
broken.Edges.Add(new() { From = "out", To = "condition" });
Assert(!runtime.Validate(broken).Valid, "cycle rejected");
broken = runtime.GetConfiguration();
broken.Nodes[0].OutputType = "text";
broken.Nodes[1].InputType = "number";
Assert(!runtime.Validate(broken).Valid, "incompatible known types rejected");
broken = runtime.GetConfiguration();
broken.Nodes[0].OutputType = "any";
broken.Nodes[1].InputType = "number";
Assert(runtime.Validate(broken).Valid, "any remains compatible without narrowing");
broken.DryRun = true;
Assert((await runtime.ApplyAsync(broken)).Valid, "typed runtime config applied");
while (trace.Reader.TryRead(out _)) { }
await runtime.ProcessAsync(new Signal { Module = "test", Phase = "down", Value = "text", ValueType = "text" });
diagnostics.Clear();
while (trace.Reader.TryRead(out var item)) diagnostics.Add(item);
Assert(diagnostics.Any(d => d.Stage == "error" && d.NodeId == "condition") && !diagnostics.Any(d => d.Stage == "intended"), "runtime actual type mismatch stops branch");
using var later = runtime.Diagnostics.Subscribe();
Assert(!later.Reader.TryRead(out _), "new subscriber has no replay");
var hub = new DiagnosticHub();
using var slow = hub.Subscribe();
for (var i = 0; i < 600; i++) hub.Publish(new(DateTimeOffset.UtcNow, "", "", "input", i.ToString()));
var lostSeen = false;
while (slow.Reader.TryRead(out var diagnostic)) lostSeen |= diagnostic.Stage == "error" && diagnostic.Message.Contains("88");
Assert(lostSeen, "diagnostic loss notice arrives even when burst ended");
var offlineModule = new Offline();
await using var offlineRuntime = new GraphRuntime([offlineModule], Path.Combine(AppContext.BaseDirectory, "offline.json"));
var offlineConfig = new Configuration { Connections = [new() { Id = "offline", Module = "offline", Name = "Offline" }] };
await offlineRuntime.StartAsync(offlineConfig);
Assert(offlineRuntime.GetConfiguration().Connections[0].Enabled, "offline startup preserves enabled configuration and runtime");
Assert(!(await offlineRuntime.ApplyAsync(offlineConfig)).Valid, "explicit apply reports unavailable connection");
offlineModule.Available = true;
Assert((await offlineRuntime.ApplyAsync(offlineConfig)).Valid && offlineModule.Attempts == 3, "same configuration retries failed connection");
await EventChecks.RunAsync();
await EditingShortcutChecks.RunAsync();
Console.WriteLine("RUNTIME_SMOKE_OK");

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}

sealed class Fake : NaModule, IDiscoveryCapture
{
    public int Count;
    public string? Last;
    public string[] CaptureInterfaces { get; private set; } = [];
    public override ModuleDescriptor Descriptor => new("fake", "Fake", "1", false, false, [new("do", "Do", ["arg"])], []);
    public ValueTask<string[]> SetDiscoveryAsync(Guid owner, string[] interfaces, CancellationToken cancellationToken)
    {
        CaptureInterfaces = interfaces;
        return ValueTask.FromResult(interfaces);
    }
    public override ValueTask<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken = default)
    {
        Count++; Last = request.Arguments["arg"];
        return ValueTask.FromResult(new ActionResult(true, "fake complete"));
    }
}

sealed class Offline : NaModule
{
    public bool Available;
    public int Attempts;
    public override ModuleDescriptor Descriptor => new("offline", "Offline", "1", false, false, [], []);
    public override ValueTask ConnectAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        Attempts++;
        if (!Available) throw new IOException("offline");
        return ValueTask.CompletedTask;
    }
}
