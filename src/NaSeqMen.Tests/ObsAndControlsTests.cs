using System.Text.Json;
using NaSeqMen.Nodes.Obs;

namespace NaSeqMen.Tests;

internal static class ObsAndControlsTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> GetTests() =>
    [
        ("1. End-to-end OBS v5 auth, VU meters, virtual controls & slider feedback sync", Test1_EndToEndObsAndControlsFlow),
        ("2. OBS v5 authentication failure reports auth failed state (4009)", Test2_ObsAuthFailure),
    ];

    private static async Task Test1_EndToEndObsAndControlsFlow()
    {
        await using var obsServer = new MockObsServer(password: "secret123");
        await using var engine = TestHarness.CreateEngine();

        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("obs", "obs.studio",
                    ("enabled", true),
                    ("host", "127.0.0.1"),
                    ("port", obsServer.Port),
                    ("password", "secret123"),
                    ("audioSource", "Mic/Aux"),
                    ("subscribeMeters", true),
                    ("meterThrottleMs", 0),
                    ("volumeScale", "percent")),
                TestHarness.Node("vu", "controls.vu-meter",
                    ("sourceFilter", "Mic/Aux"), ("min", 0), ("max", 100), ("unit", "%")),
                TestHarness.Node("btnMute", "controls.button",
                    ("mode", "momentary"), ("payload", "toggle"), ("buttonLabel", "Mute Mic")),
                TestHarness.Node("btnScene", "controls.button",
                    ("mode", "momentary"), ("payload", "BRB"), ("buttonLabel", "BRB Scene")),
                TestHarness.Node("btnRec", "controls.button",
                    ("mode", "momentary"), ("payload", "toggle")),
                TestHarness.Node("btnStream", "controls.button",
                    ("mode", "momentary"), ("payload", "toggle")),
                TestHarness.Node("slider", "controls.slider",
                    ("min", 0), ("max", 100), ("step", 1), ("defaultValue", 75), ("unit", "%")),
                TestHarness.Node("dbgVu", "basic.debug", ("label", "vu")),
            ],
            [
                TestHarness.Edge("obs", "vuLevel", "vu", "in"),
                TestHarness.Edge("vu", "out", "dbgVu", "in"),
                TestHarness.Edge("btnMute", "press", "obs", "mute"),
                TestHarness.Edge("btnScene", "press", "obs", "scene"),
                TestHarness.Edge("btnRec", "press", "obs", "record"),
                TestHarness.Edge("btnStream", "press", "obs", "stream"),
                TestHarness.Edge("slider", "value", "obs", "volume"),
                TestHarness.Edge("obs", "volumeChanged", "slider", "set"),
            ]);

        await engine.DeployAsync(ws);

        // 1. Wait for OBS authentication & initial metadata widget population
        await TestHarness.WaitUntilAsync(() =>
        {
            var w = TestHarness.GetWidgetJson(engine, "obs");
            return w.HasValue &&
                   w.Value.GetProperty("connected").GetBoolean() &&
                   w.Value.GetProperty("scenes").GetArrayLength() == 2 &&
                   w.Value.GetProperty("inputs").GetArrayLength() == 2;
        }, timeoutMs: 5000, failMessage: "OBS node did not connect and populate scenes/inputs widget state.");

        Assert.True((obsServer.LastEventSubscriptions & ObsWebSocketClient.EventSubscriptionInputVolumeMeters) != 0,
            $"Expected InputVolumeMeters bit (65536) in eventSubscriptions, got {obsServer.LastEventSubscriptions}.");

        var obsWidget = TestHarness.GetWidgetJson(engine, "obs")!.Value;
        Assert.Equal("30.2.0", obsWidget.GetProperty("obsVersion").GetString(), "obsVersion");
        var scenes = obsWidget.GetProperty("scenes").EnumerateArray().Select(e => e.GetString()).ToArray();
        var inputs = obsWidget.GetProperty("inputs").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.True(scenes.Contains("Main") && scenes.Contains("BRB"), "Expected Main and BRB scenes");
        Assert.True(inputs.Contains("Mic/Aux") && inputs.Contains("Desktop Audio"), "Expected Mic/Aux and Desktop Audio inputs");

        // 2. Push stereo InputVolumeMeters -> flows into VuMeterNode and DebugNode
        await obsServer.SendVolumeMetersAsync("Mic/Aux", [[0.5, 0.5, 0.5], [0.25, 0.25, 0.25]]);

        await TestHarness.WaitUntilAsync(() =>
        {
            var w = TestHarness.GetWidgetJson(engine, "vu");
            return w.HasValue && w.Value.GetProperty("peak").GetDouble() > 0;
        }, failMessage: "VuMeterNode did not receive InputVolumeMeters update.");

        var vuWidget = TestHarness.GetWidgetJson(engine, "vu")!.Value;
        Assert.Close(90.0, vuWidget.GetProperty("left").GetDouble(), 0.2, "VU left");
        Assert.Close(80.0, vuWidget.GetProperty("right").GetDouble(), 0.2, "VU right");
        Assert.True(vuWidget.GetProperty("peak").GetDouble() > 0, "VU peak > 0");
        Assert.Close(-6.0, vuWidget.GetProperty("db").GetDouble(), 0.2, "VU db");
        Assert.Equal("Mic/Aux", vuWidget.GetProperty("source").GetString(), "VU source");

        await TestHarness.WaitUntilAsync(() => engine.GetDebugHistory().Any(d => d.Label == "vu"),
            failMessage: "DebugNode did not receive pass-through VU message.");

        // 3. VirtualButtonNode press -> ToggleInputMute
        await engine.InteractNodeAsync("btnMute", "press");
        await TestHarness.WaitUntilAsync(
            () => obsServer.GetRequests().Any(r => r.RequestType == "ToggleInputMute"),
            failMessage: "ToggleInputMute request was not received by Mock OBS.");
        var muteReq = obsServer.GetRequests().Last(r => r.RequestType == "ToggleInputMute");
        Assert.Equal("Mic/Aux", muteReq.RequestData!.Value.GetProperty("inputName").GetString(), "ToggleInputMute inputName");

        // 4. VirtualSliderNode change (value = 42) -> SetInputVolume
        await engine.InteractNodeAsync("slider", "change", TestHarness.ToJsonElement(new { value = 42 }));
        await TestHarness.WaitUntilAsync(
            () => obsServer.GetRequests().Any(r => r.RequestType == "SetInputVolume"),
            failMessage: "SetInputVolume request was not received by Mock OBS.");
        var volReq = obsServer.GetRequests().Last(r => r.RequestType == "SetInputVolume");
        Assert.Equal("Mic/Aux", volReq.RequestData!.Value.GetProperty("inputName").GetString(), "SetInputVolume inputName");
        Assert.Close(-34.8, volReq.RequestData!.Value.GetProperty("inputVolumeDb").GetDouble(), 0.2, "42% -> -34.8 dB");

        // 5. ObsStudioNode scene input -> SetCurrentProgramScene (+ ToggleRecord & ToggleStream)
        await engine.InteractNodeAsync("btnScene", "press");
        await engine.InteractNodeAsync("btnRec", "press");
        await engine.InteractNodeAsync("btnStream", "press");
        await TestHarness.WaitUntilAsync(
            () => obsServer.GetRequests().Any(r => r.RequestType == "SetCurrentProgramScene") &&
                  obsServer.GetRequests().Any(r => r.RequestType == "ToggleRecord") &&
                  obsServer.GetRequests().Any(r => r.RequestType == "ToggleStream"),
            failMessage: "Expected SetCurrentProgramScene, ToggleRecord, and ToggleStream requests.");
        var sceneReq = obsServer.GetRequests().Last(r => r.RequestType == "SetCurrentProgramScene");
        Assert.Equal("BRB", sceneReq.RequestData!.Value.GetProperty("sceneName").GetString(), "sceneName");

        // 6. Slider feedback sync from OBS InputVolumeChanged -> slider.set (no infinite loop!)
        var volCountBefore = obsServer.GetRequests().Count(r => r.RequestType == "SetInputVolume");
        await obsServer.SendVolumeChangedAsync("Mic/Aux", inputVolumeMul: 0.5, inputVolumeDb: -6.0);

        await TestHarness.WaitUntilAsync(() =>
        {
            var w = TestHarness.GetWidgetJson(engine, "slider");
            return w.HasValue && Math.Abs(w.Value.GetProperty("value").GetDouble() - 90.0) < 0.2;
        }, failMessage: "VirtualSliderNode did not sync value from OBS InputVolumeChanged feedback.");

        // Wait briefly and confirm SetInputVolume was NOT re-triggered
        await Task.Delay(120);
        var volCountAfter = obsServer.GetRequests().Count(r => r.RequestType == "SetInputVolume");
        Assert.Equal(volCountBefore, volCountAfter, "Feedback sync via slider.set must not re-emit SetInputVolume");
    }

    private static async Task Test2_ObsAuthFailure()
    {
        await using var obsServer = new MockObsServer(password: "secret123");
        await using var engine = TestHarness.CreateEngine();

        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("obs", "obs.studio",
                    ("enabled", true),
                    ("host", "127.0.0.1"),
                    ("port", obsServer.Port),
                    ("password", "wrong-password"),
                    ("subscribeMeters", true)),
            ],
            []);

        await engine.DeployAsync(ws);

        await TestHarness.WaitUntilAsync(() =>
        {
            var statuses = engine.GetNodeStatuses();
            return obsServer.AuthFailureCount >= 1 &&
                   statuses.TryGetValue("obs", out var st) &&
                   string.Equals(st, "auth failed", StringComparison.OrdinalIgnoreCase);
        }, timeoutMs: 5000, failMessage: "Expected OBS node to report 'auth failed' on invalid password.");
    }
}
