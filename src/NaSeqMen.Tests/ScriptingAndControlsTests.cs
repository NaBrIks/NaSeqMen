namespace NaSeqMen.Tests;

internal static class ScriptingAndControlsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> GetTests() =>
    [
        ("1. CSharpScriptNode mutates basket with Suppress(true) & SendKeys(\"LCtrl+V\")", Test1_ScriptMutatesBasket),
        ("2. CSharpScriptNode per-node state persistence & isolation across calls", Test2_ScriptStatePersistence),
        ("3. CSharpScriptNode compile error & runtime error isolation", Test3_ScriptErrorIsolation),
        ("4. VirtualButtonNode momentary vs toggle modes & set feedback", Test4_VirtualButtonModes),
        ("5. VuMeterNode sourceFilter filters non-matching sources", Test5_VuMeterSourceFilter),
    ];

    private static async Task Test1_ScriptMutatesBasket()
    {
        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("btn", "controls.button",
                    ("mode", "momentary"), ("payload", "D"), ("topic", "interception.keyboard")),
                TestHarness.Node("script", "script.csharp",
                    ("code", "Suppress(true); SendKeys(\"LCtrl+V\"); props[\"handledBy\"] = \"roslyn\"; return msg;")),
                TestHarness.Node("dbgOut", "basic.debug", ("label", "out")),
                TestHarness.Node("dbgErr", "basic.debug", ("label", "err")),
            ],
            [
                TestHarness.Edge("btn", "press", "script", "in"),
                TestHarness.Edge("script", "out", "dbgOut", "in"),
                TestHarness.Edge("script", "error", "dbgErr", "in"),
            ]);

        await engine.DeployAsync(ws);
        await engine.InteractNodeAsync("btn", "press");

        var history = engine.GetDebugHistory();
        Assert.Equal(0, history.Count(d => d.Label == "err"), "Script should not emit errors");
        var outEntry = history.LastOrDefault(d => d.Label == "out");
        Assert.NotNull(outEntry, "Expected script output message");
        Assert.Equal("D", outEntry!.Payload?.ToString(), "Payload preserved");
        Assert.NotNull(outEntry.Properties, "Full basket properties missing");
        Assert.Equal(true, outEntry.Properties!["suppress"], "suppress flag");
        Assert.Equal("LCtrl+V", outEntry.Properties["sendKeys"], "sendKeys property");
        Assert.Equal("roslyn", outEntry.Properties["handledBy"], "custom property");
    }

    private static async Task Test2_ScriptStatePersistence()
    {
        await using var engine = TestHarness.CreateEngine();
        const string counterCode = """
            var count = state.TryGetValue("count", out var raw) && raw is int i ? i + 1 : 1;
            state["count"] = count;
            return count;
            """;

        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("triggerA", "controls.button", ("mode", "momentary"), ("payload", "go")),
                TestHarness.Node("triggerB", "controls.button", ("mode", "momentary"), ("payload", "go")),
                TestHarness.Node("scriptA", "script.csharp", ("code", counterCode)),
                TestHarness.Node("scriptB", "script.csharp", ("code", counterCode)),
                TestHarness.Node("dbgA", "basic.debug", ("label", "A")),
                TestHarness.Node("dbgB", "basic.debug", ("label", "B")),
            ],
            [
                TestHarness.Edge("triggerA", "press", "scriptA", "in"),
                TestHarness.Edge("scriptA", "out", "dbgA", "in"),
                TestHarness.Edge("triggerB", "press", "scriptB", "in"),
                TestHarness.Edge("scriptB", "out", "dbgB", "in"),
            ]);

        await engine.DeployAsync(ws);

        await engine.InteractNodeAsync("triggerA", "press");
        await engine.InteractNodeAsync("triggerA", "press");
        await engine.InteractNodeAsync("triggerA", "press");
        await engine.InteractNodeAsync("triggerB", "press");

        var aValues = engine.GetDebugHistory().Where(d => d.Label == "A").Select(d => Convert.ToInt32(d.Payload)).ToArray();
        var bValues = engine.GetDebugHistory().Where(d => d.Label == "B").Select(d => Convert.ToInt32(d.Payload)).ToArray();

        Assert.Equal(3, aValues.Length, "scriptA output count");
        Assert.Equal(1, aValues[0], "scriptA call 1");
        Assert.Equal(2, aValues[1], "scriptA call 2");
        Assert.Equal(3, aValues[2], "scriptA call 3");

        Assert.Equal(1, bValues.Length, "scriptB output count");
        Assert.Equal(1, bValues[0], "scriptB must have isolated per-node state");
    }

    private static async Task Test3_ScriptErrorIsolation()
    {
        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("btnCompile", "controls.button", ("mode", "momentary"), ("payload", "x")),
                TestHarness.Node("badCompile", "script.csharp", ("code", "int broken = ;")),
                TestHarness.Node("dbgCompileOut", "basic.debug", ("label", "compile-out")),
                TestHarness.Node("dbgCompileErr", "basic.debug", ("label", "compile-err")),

                TestHarness.Node("btnRuntime", "controls.button", ("mode", "momentary"), ("payload", "y")),
                TestHarness.Node("badRuntime", "script.csharp", ("code", "throw new InvalidOperationException(\"boom-runtime\");")),
                TestHarness.Node("dbgRuntimeOut", "basic.debug", ("label", "runtime-out")),
                TestHarness.Node("dbgRuntimeErr", "basic.debug", ("label", "runtime-err")),

                TestHarness.Node("btnGood", "controls.button", ("mode", "momentary"), ("payload", "ok")),
                TestHarness.Node("goodScript", "script.csharp", ("code", "return text.ToUpperInvariant();")),
                TestHarness.Node("dbgGoodOut", "basic.debug", ("label", "good-out")),
            ],
            [
                TestHarness.Edge("btnCompile", "press", "badCompile", "in"),
                TestHarness.Edge("badCompile", "out", "dbgCompileOut", "in"),
                TestHarness.Edge("badCompile", "error", "dbgCompileErr", "in"),

                TestHarness.Edge("btnRuntime", "press", "badRuntime", "in"),
                TestHarness.Edge("badRuntime", "out", "dbgRuntimeOut", "in"),
                TestHarness.Edge("badRuntime", "error", "dbgRuntimeErr", "in"),

                TestHarness.Edge("btnGood", "press", "goodScript", "in"),
                TestHarness.Edge("goodScript", "out", "dbgGoodOut", "in"),
            ]);

        await engine.DeployAsync(ws);

        await engine.InteractNodeAsync("btnCompile", "press");
        await engine.InteractNodeAsync("btnRuntime", "press");
        await engine.InteractNodeAsync("btnGood", "press");

        var history = engine.GetDebugHistory();

        Assert.Equal(0, history.Count(d => d.Label == "compile-out"), "Compile-broken script must not emit on out");
        var compileErr = history.LastOrDefault(d => d.Label == "compile-err");
        Assert.NotNull(compileErr, "Compile-broken script must emit on error port");
        Assert.Equal("compile", compileErr!.Properties!["errorKind"], "compile errorKind");

        Assert.Equal(0, history.Count(d => d.Label == "runtime-out"), "Runtime-failing script must not emit on out");
        var runtimeErr = history.LastOrDefault(d => d.Label == "runtime-err");
        Assert.NotNull(runtimeErr, "Runtime-failing script must emit on error port");
        Assert.Equal("runtime", runtimeErr!.Properties!["errorKind"], "runtime errorKind");
        Assert.Contains("boom-runtime", runtimeErr.Payload?.ToString() ?? "", "runtime error message");

        var goodOut = history.LastOrDefault(d => d.Label == "good-out");
        Assert.NotNull(goodOut, "Healthy sibling node must keep working after errors");
        Assert.Equal("OK", goodOut!.Payload?.ToString(), "Healthy script output");
    }

    private static async Task Test4_VirtualButtonModes()
    {
        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("mom", "controls.button",
                    ("mode", "momentary"), ("payload", "true"), ("offPayload", "false"), ("buttonLabel", "Hold")),
                TestHarness.Node("tog", "controls.button",
                    ("mode", "toggle"), ("payload", "ON_VAL"), ("offPayload", "OFF_VAL"), ("buttonLabel", "Toggle")),
                TestHarness.Node("syncDriver", "controls.button",
                    ("mode", "momentary"), ("payload", "true")),
                TestHarness.Node("dbgMomPress", "basic.debug", ("label", "mom-press")),
                TestHarness.Node("dbgMomRel", "basic.debug", ("label", "mom-rel")),
                TestHarness.Node("dbgTogPress", "basic.debug", ("label", "tog-press")),
                TestHarness.Node("dbgTogRel", "basic.debug", ("label", "tog-rel")),
            ],
            [
                TestHarness.Edge("mom", "press", "dbgMomPress", "in"),
                TestHarness.Edge("mom", "release", "dbgMomRel", "in"),
                TestHarness.Edge("tog", "press", "dbgTogPress", "in"),
                TestHarness.Edge("tog", "release", "dbgTogRel", "in"),
                TestHarness.Edge("syncDriver", "press", "tog", "set"),
            ]);

        await engine.DeployAsync(ws);

        // Momentary: press emits on press (active=true), release emits on release (active=false)
        await engine.InteractNodeAsync("mom", "press");
        Assert.True(TestHarness.GetWidgetJson(engine, "mom")!.Value.GetProperty("active").GetBoolean(), "momentary active on press");
        await engine.InteractNodeAsync("mom", "release");
        Assert.False(TestHarness.GetWidgetJson(engine, "mom")!.Value.GetProperty("active").GetBoolean(), "momentary inactive on release");

        var momPress = engine.GetDebugHistory().Where(d => d.Label == "mom-press").ToArray();
        var momRel = engine.GetDebugHistory().Where(d => d.Label == "mom-rel").ToArray();
        Assert.Equal(1, momPress.Length, "momentary press count");
        Assert.Equal(true, momPress[0].Payload, "momentary press payload");
        Assert.Equal(1, momRel.Length, "momentary release count");
        Assert.Equal(false, momRel[0].Payload, "momentary release payload");

        // Toggle: first press -> ON_VAL, release ignored, second press -> OFF_VAL
        await engine.InteractNodeAsync("tog", "press");
        Assert.True(TestHarness.GetWidgetJson(engine, "tog")!.Value.GetProperty("active").GetBoolean(), "toggle active after 1st press");
        await engine.InteractNodeAsync("tog", "release");
        await engine.InteractNodeAsync("tog", "press");
        Assert.False(TestHarness.GetWidgetJson(engine, "tog")!.Value.GetProperty("active").GetBoolean(), "toggle inactive after 2nd press");

        var togPress = engine.GetDebugHistory().Where(d => d.Label == "tog-press").ToArray();
        var togRel = engine.GetDebugHistory().Where(d => d.Label == "tog-rel").ToArray();
        Assert.Equal(2, togPress.Length, "toggle press count");
        Assert.Equal("ON_VAL", togPress[0].Payload?.ToString(), "toggle 1st payload");
        Assert.Equal("OFF_VAL", togPress[1].Payload?.ToString(), "toggle 2nd payload");
        Assert.Equal(0, togRel.Length, "toggle mode must not emit on release");

        // External feedback sync via set port updates visual state without emitting
        await engine.InteractNodeAsync("syncDriver", "press");
        Assert.True(TestHarness.GetWidgetJson(engine, "tog")!.Value.GetProperty("active").GetBoolean(), "toggle synced active via set");
        Assert.Equal(2, engine.GetDebugHistory().Count(d => d.Label == "tog-press"), "set feedback must not re-emit press");
    }

    private static async Task Test5_VuMeterSourceFilter()
    {
        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("emitOther", "controls.button",
                    ("mode", "momentary"), ("payload", "85"), ("topic", "Desktop Audio")),
                TestHarness.Node("emitMic", "controls.button",
                    ("mode", "momentary"), ("payload", "64"), ("topic", "Mic/Aux")),
                TestHarness.Node("vu", "controls.vu-meter",
                    ("sourceFilter", "Mic"), ("min", 0), ("max", 100), ("unit", "%")),
                TestHarness.Node("dbgVu", "basic.debug", ("label", "vu-out")),
            ],
            [
                TestHarness.Edge("emitOther", "press", "vu", "in"),
                TestHarness.Edge("emitMic", "press", "vu", "in"),
                TestHarness.Edge("vu", "out", "dbgVu", "in"),
            ]);

        await engine.DeployAsync(ws);

        // Non-matching source must be filtered out
        await engine.InteractNodeAsync("emitOther", "press");
        var wBefore = TestHarness.GetWidgetJson(engine, "vu")!.Value;
        Assert.Close(0.0, wBefore.GetProperty("peak").GetDouble(), 0.01, "Non-matching source must not update VU peak");
        Assert.Equal(0, engine.GetDebugHistory().Count(d => d.Label == "vu-out"), "Non-matching source must not pass through");

        // Matching source updates widget and passes through
        await engine.InteractNodeAsync("emitMic", "press");
        var wAfter = TestHarness.GetWidgetJson(engine, "vu")!.Value;
        Assert.Close(64.0, wAfter.GetProperty("level").GetDouble(), 0.1, "Matching source VU level");
        Assert.Close(64.0, wAfter.GetProperty("peak").GetDouble(), 0.1, "Matching source VU peak");
        Assert.Equal("Mic/Aux", wAfter.GetProperty("source").GetString(), "Matching source name");
        Assert.Equal(1, engine.GetDebugHistory().Count(d => d.Label == "vu-out"), "Matching source must pass through");
    }
}
