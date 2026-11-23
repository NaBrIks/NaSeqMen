using NaSeqMen.Core.Graph;
using NaSeqMen.Nodes.Interception;

namespace NaSeqMen.Tests;

internal static class HotkeySwitchTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> GetTests() =>
    [
        ("1. Multiple rules sharing prefix modifier (LWin+D -> LCtrl+X, LWin+E -> LCtrl+K, LCtrl+D)", TestSharedPrefixChordsAsync),
        ("2. Unmatched chord atomically replays modifier + key in order (LWin+R)", TestUnmatchedChordReplayAsync),
        ("3. Bare modifier tap replays LWin down+up on release", TestBareModifierTapReplayAsync),
        ("4. Held modifier auto-replays after chordWindowMs timeout", TestTimeoutAutoReplayAsync),
        ("5. Mouse button rule (X1 -> LCtrl+C) inside HotkeySwitch", TestMouseRuleInSwitchAsync),
        ("6. InputProbe monitors passively, blocks when armed, and disarms on Esc", TestInputProbeBlockAndEscAsync),
        ("7. InputProbe auto-disarms after inactivity timeout", TestInputProbeIdleTimeoutAsync),
        ("8. Hub All + KB1 parallel branches merge cleanly without race (Monitor All + Probe Override)", TestHubAllParallelMergeAsync),
    ];

    private static InterceptionStroke KeyStroke(string keyName, bool isDown)
    {
        Assert.True(ScanCodeMap.TryGetScanCode(keyName, out var sc, out var isE0), $"Unknown key: {keyName}");
        ushort state = isDown ? InterceptionConstants.KeyDown : InterceptionConstants.KeyUp;
        if (isE0) state |= InterceptionConstants.KeyE0;
        return new InterceptionStroke { Key = new InterceptionKeyStroke { Code = sc, State = state } };
    }

    private static InterceptionStroke MouseButtonStroke(string buttonName, bool isDown)
    {
        Assert.True(ScanCodeMap.TryGetMouseButton(buttonName, out _, out var down, out var up, out var rolling), $"Unknown button: {buttonName}");
        return new InterceptionStroke
        {
            Mouse = new InterceptionMouseStroke { State = isDown ? down : up, Rolling = rolling },
        };
    }

    private static string DescribeKeyStroke((int Device, InterceptionStroke Stroke) entry)
    {
        var k = entry.Stroke.Key;
        var isE0 = (k.State & InterceptionConstants.KeyE0) != 0;
        var isUp = (k.State & InterceptionConstants.KeyUp) != 0;
        var name = ScanCodeMap.GetKeyName(k.Code, isE0);
        return $"{name}:{(isUp ? "up" : "down")}";
    }

    private static WorkspaceDocument CreateSwitchWorkspace(
        object rules,
        string deviceKind = "keyboard",
        string hwid = "HID\\VID_TEST&PID_0001&MI_00",
        bool replayUnmatched = true,
        int chordWindowMs = 400)
    {
        return TestHarness.Workspace(
            [
                TestHarness.Node(
                    "ic-dev",
                    "interception.device",
                    ("active", true),
                    ("deviceKind", deviceKind),
                    ("hardwareIdFilter", hwid),
                    ("defaultAction", "pass"),
                    ("decisionTimeoutMs", 250)),
                TestHarness.Node(
                    "hsw",
                    "interception.hotkey-switch",
                    ("rules", rules),
                    ("replayUnmatchedModifier", replayUnmatched),
                    ("chordWindowMs", chordWindowMs)),
            ],
            [
                TestHarness.Edge("ic-dev", "out", "hsw", "in"),
                TestHarness.Edge("hsw", "return", "ic-dev", "return"),
            ]);
    }

    private static async Task TestSharedPrefixChordsAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(2, "HID\\VID_TEST&PID_0001&MI_00");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "LWin+D", to = "LCtrl+X", mode = "tapOnDown", enabled = true },
                new { from = "LWin+E", to = "LCtrl+K, LCtrl+D", mode = "tapOnDown", enabled = true },
            };
            await engine.DeployAsync(CreateSwitchWorkspace(rules));
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(2) != 0);

            // Chord 1: LWin+D -> LCtrl+X
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", true));
            Assert.Equal(0, backend.SnapshotSent().Count, "LWin down must be buffered/suppressed");

            await backend.InjectAndWaitAsync(2, KeyStroke("D", true));
            await backend.InjectAndWaitAsync(2, KeyStroke("D", false));
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", false));

            var sent1 = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LCtrl:down,X:down,X:up,LCtrl:up", string.Join(",", sent1), "Expected LCtrl+X tap only");

            backend.ClearSent();

            // Chord 2: LWin+E -> LCtrl+K, LCtrl+D
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", true));
            await backend.InjectAndWaitAsync(2, KeyStroke("E", true));
            await backend.InjectAndWaitAsync(2, KeyStroke("E", false));
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", false));

            var sent2 = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LCtrl:down,K:down,K:up,LCtrl:up,LCtrl:down,D:down,D:up,LCtrl:up", string.Join(",", sent2), "Expected two-step sequence LCtrl+K, LCtrl+D");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestUnmatchedChordReplayAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(2, "HID\\VID_TEST&PID_0001&MI_00");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "LWin+D", to = "LCtrl+X", mode = "tapOnDown", enabled = true },
            };
            await engine.DeployAsync(CreateSwitchWorkspace(rules, replayUnmatched: true, chordWindowMs: 500));
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(2) != 0);

            // Press LWin (buffered), then press R (unmatched!) -> must replay [LWin:down, R:down] in order!
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", true));
            Assert.Equal(0, backend.SnapshotSent().Count, "LWin down should wait in buffer");

            await backend.InjectAndWaitAsync(2, KeyStroke("R", true));
            await backend.InjectAndWaitAsync(2, KeyStroke("R", false));
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", false));

            var sent = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LWin:down,R:down,R:up,LWin:up", string.Join(",", sent), "Unmatched Win+R must replay cleanly in exact order");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestBareModifierTapReplayAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(2, "HID\\VID_TEST&PID_0001&MI_00");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "LWin+D", to = "LCtrl+X", mode = "tapOnDown", enabled = true },
            };
            await engine.DeployAsync(CreateSwitchWorkspace(rules, replayUnmatched: true, chordWindowMs: 500));
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(2) != 0);

            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", true));
            Assert.Equal(0, backend.SnapshotSent().Count);

            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", false));
            var sent = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LWin:down,LWin:up", string.Join(",", sent), "Bare LWin tap must replay down+up");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestTimeoutAutoReplayAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(2, "HID\\VID_TEST&PID_0001&MI_00");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "LWin+D", to = "LCtrl+X", mode = "tapOnDown", enabled = true },
            };
            await engine.DeployAsync(CreateSwitchWorkspace(rules, replayUnmatched: true, chordWindowMs: 80));
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(2) != 0);

            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", true));
            Assert.Equal(0, backend.SnapshotSent().Count, "Should be buffered initially");

            await TestHarness.WaitUntilAsync(() => backend.SnapshotSent().Count >= 1, timeoutMs: 1000);
            await backend.InjectAndWaitAsync(2, KeyStroke("LWin", false));

            var sent = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LWin:down,LWin:up", string.Join(",", sent), "Timeout should replay LWin:down, then real LWin:up passes through");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestMouseRuleInSwitchAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(1, "HID\\VID_TEST&PID_0001&MI_00");
        backend.SetHardwareId(12, "HID\\VID_TEST&PID_0001&MI_01");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "X1", to = "LCtrl+C", mode = "tapOnDown", enabled = true },
                new { from = "X2", to = "LCtrl+V", mode = "tapOnDown", enabled = true },
            };
            await engine.DeployAsync(CreateSwitchWorkspace(rules, deviceKind: "mouse", hwid: "HID\\VID_TEST&PID_0001&MI_01"));
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(12) != 0);

            await backend.InjectAndWaitAsync(12, MouseButtonStroke("X1", true));
            await backend.InjectAndWaitAsync(12, MouseButtonStroke("X1", false));

            var sent = backend.SnapshotSent();
            Assert.True(sent.All(s => s.Device == 1), "Keyboard shortcut must route to sibling keyboard slot 1");
            Assert.Equal("LCtrl:down,C:down,C:up,LCtrl:up", string.Join(",", sent.Select(DescribeKeyStroke)));
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestInputProbeBlockAndEscAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(3, "HID\\VID_TEST&PID_0002&MI_00");
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var doc = TestHarness.Workspace(
                [
                    TestHarness.Node(
                        "ic-dev",
                        "interception.device",
                        ("active", true),
                        ("deviceKind", "keyboard"),
                        ("hardwareIdFilter", "HID\\VID_TEST&PID_0002&MI_00"),
                        ("defaultAction", "pass"),
                        ("decisionTimeoutMs", 250)),
                    TestHarness.Node(
                        "probe",
                        "interception.probe",
                        ("blockWhenArmed", true),
                        ("idleTimeoutSec", 15),
                        ("maxDurationSec", 45)),
                ],
                [
                    TestHarness.Edge("ic-dev", "out", "probe", "in"),
                    TestHarness.Edge("probe", "return", "ic-dev", "return"),
                ]);

            await engine.DeployAsync(doc);
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(3) != 0);

            // 1. Disarmed (passive monitor): A passes through and is logged
            await backend.InjectAndWaitAsync(3, KeyStroke("A", true));
            Assert.Equal("A:down", string.Join(",", backend.SnapshotSent().Select(DescribeKeyStroke)), "Passive probe must pass A through");

            // 2. Arm probe -> B is blocked (suppressed)
            backend.ClearSent();
            await engine.InteractNodeAsync("probe", "arm");
            await backend.InjectAndWaitAsync(3, KeyStroke("B", true));
            Assert.Equal(0, backend.SnapshotSent().Count, "Armed blocking probe must suppress B");

            // 3. Press Esc -> instantly disarms and passes Esc through!
            await backend.InjectAndWaitAsync(3, KeyStroke("Esc", true));
            Assert.Equal("Esc:down", string.Join(",", backend.SnapshotSent().Select(DescribeKeyStroke)), "Esc must pass through and disarm");

            var probeState = TestHarness.GetWidgetJson(engine, "probe")!.Value;
            Assert.Equal(false, probeState.GetProperty("armed").GetBoolean(), "Probe must be disarmed after Esc");
            Assert.True(probeState.GetProperty("lines").GetArrayLength() >= 3, "Terminal should contain logged lines");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static async Task TestInputProbeIdleTimeoutAsync()
    {
        await using var engine = TestHarness.CreateEngine();
        var doc = TestHarness.Workspace(
            [
                TestHarness.Node(
                    "probe",
                    "interception.probe",
                    ("blockWhenArmed", true),
                    ("idleTimeoutSec", 1),
                    ("maxDurationSec", 10)),
            ],
            []);

        await engine.DeployAsync(doc);
        await engine.InteractNodeAsync("probe", "arm");

        var armedState = TestHarness.GetWidgetJson(engine, "probe")!.Value;
        Assert.Equal(true, armedState.GetProperty("armed").GetBoolean(), "Probe should start armed");

        await TestHarness.WaitUntilAsync(() =>
            !(TestHarness.GetWidgetJson(engine, "probe")?.GetProperty("armed").GetBoolean() ?? true),
            timeoutMs: 2500);

        var finalState = TestHarness.GetWidgetJson(engine, "probe")!.Value;
        Assert.Equal("idle timeout", finalState.GetProperty("reason").GetString(), "Should auto-disarm with idle timeout");
    }

    private static async Task TestHubAllParallelMergeAsync()
    {
        var backend = new MockInterceptionBackend();
        backend.SetHardwareId(1, "HID\\VID_046D&PID_C52B&MI_00"); // Logitech (bound to kb1)
        backend.SetHardwareId(2, "HID\\VID_09DA&PID_9090&MI_00"); // A4Tech (unconfigured, seen via All!)
        InterceptionDriverService.Instance.SetBackendForTesting(backend);
        try
        {
            await using var engine = TestHarness.CreateEngine();
            var rules = new[]
            {
                new { from = "LWin+D", to = "LCtrl+X", mode = "tapOnDown", enabled = true },
            };
            var doc = TestHarness.Workspace(
                [
                    TestHarness.Node(
                        "hub",
                        "interception.hub",
                        ("active", true),
                        ("monitorAll", true),
                        ("kb1Filter", "VID_046D"),
                        ("defaultAction", "pass"),
                        ("decisionTimeoutMs", 250)),
                    TestHarness.Node(
                        "hsw",
                        "interception.hotkey-switch",
                        ("rules", rules),
                        ("replayUnmatchedModifier", true),
                        ("chordWindowMs", 400)),
                    TestHarness.Node(
                        "probe",
                        "interception.probe",
                        ("blockWhenArmed", true),
                        ("idleTimeoutSec", 15)),
                ],
                [
                    TestHarness.Edge("hub", "kb1", "hsw", "in"),
                    TestHarness.Edge("hsw", "return", "hub", "return"),
                    TestHarness.Edge("hub", "all", "probe", "in"),
                    TestHarness.Edge("probe", "return", "hub", "return"),
                ]);

            await engine.DeployAsync(doc);
            await TestHarness.WaitUntilAsync(() => backend.GetSlotFilter(1) != 0 && backend.GetSlotFilter(2) != 0);

            // 1. Passive Probe + active Hotkey Switch on KB1: ZERO race!
            // Probe sees LWin+D, Hotkey Switch suppresses LWin+D and sends LCtrl+X.
            await backend.InjectAndWaitAsync(1, KeyStroke("LWin", true));
            await backend.InjectAndWaitAsync(1, KeyStroke("D", true));
            await backend.InjectAndWaitAsync(1, KeyStroke("D", false));
            await backend.InjectAndWaitAsync(1, KeyStroke("LWin", false));

            var sent1 = backend.SnapshotSent().Select(DescribeKeyStroke).ToArray();
            Assert.Equal("LCtrl:down,X:down,X:up,LCtrl:up", string.Join(",", sent1), "Hotkey Switch remap must win cleanly while passive Probe monitors");

            // 2. Unconfigured slot 2 (A4Tech) is ALSO seen by Probe via All and passes through!
            backend.ClearSent();
            await backend.InjectAndWaitAsync(2, KeyStroke("G", true));
            Assert.Equal("G:down", string.Join(",", backend.SnapshotSent().Select(DescribeKeyStroke)), "Unconfigured device passes through when Probe is passive");

            var probeJson = TestHarness.GetWidgetJson(engine, "probe")!.Value;
            var lines = probeJson.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("device").GetString() ?? "").ToArray();
            Assert.True(lines.Any(d => d.Contains("Logitech")), "Probe should display friendly brand Logitech");
            Assert.True(lines.Any(d => d.Contains("A4Tech")), "Probe should display friendly brand A4Tech for unconfigured slot 2");

            // 3. Arm Probe -> blocks BOTH original LWin+D AND Hotkey Switch's LCtrl+X replacement!
            backend.ClearSent();
            await engine.InteractNodeAsync("probe", "arm");
            await backend.InjectAndWaitAsync(1, KeyStroke("LWin", true));
            await backend.InjectAndWaitAsync(1, KeyStroke("D", true));
            Assert.Equal(0, backend.SnapshotSent().Count, "Armed blocking Probe must block both original stroke and Hotkey Switch actions");
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }
}
