using NaSeqMen.Nodes.Interception;

namespace NaSeqMen.Tests;

internal static class InterceptionLoopTests
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> GetTests() =>
    [
        ("1. HWID resolution across USB port/slot change", Test1_HwidResolutionAcrossSlotChange),
        ("2. Loop-back chord suppression & replacement (LWin+D -> LCtrl+X)", Test2_ChordSuppressionAndReplacement),
        ("3. Mouse X1 button to keyboard shortcut loop-back (LCtrl+C)", Test3_MouseButtonToKeyboardShortcut),
        ("4. Fail-safe timeout forwards stroke unchanged", Test4_FailSafeTimeoutForwardsStroke),
        ("5. Emergency panic kill-switch (LCtrl+LAlt+Pause) clears all filters", Test5_EmergencyPanicKillSwitch),
        ("6. Interception Hub multi-port routing (kb1 + mouse1 -> return)", Test6_InterceptionHubMultiPortRouting),
    ];

    private static InterceptionStroke MakeKeyStroke(string keyName, bool isDown)
    {
        Assert.True(ScanCodeMap.TryGetScanCode(keyName, out var sc, out var isE0), $"Unknown test key '{keyName}'.");
        ushort state = isDown ? InterceptionConstants.KeyDown : InterceptionConstants.KeyUp;
        if (isE0) state |= InterceptionConstants.KeyE0;
        return new InterceptionStroke { Key = new InterceptionKeyStroke { Code = sc, State = state } };
    }

    private static InterceptionStroke MakeMouseStroke(ushort state)
        => new() { Mouse = new InterceptionMouseStroke { State = state } };

    private static string DescribeKeyStroke(in InterceptionKeyStroke k)
    {
        var name = ScanCodeMap.GetKeyNameFromState(k.Code, k.State);
        var phase = (k.State & InterceptionConstants.KeyUp) == 0 ? "down" : "up";
        return $"{name}:{phase}";
    }

    private static async Task RunIsolatedAsync(Func<MockInterceptionBackend, Task> body)
    {
        using var mock = new MockInterceptionBackend();
        InterceptionDriverService.Instance.SetBackendForTesting(mock);
        try
        {
            await body(mock);
        }
        finally
        {
            InterceptionDriverService.Instance.ResetPanic();
            InterceptionDriverService.Instance.SetBackendForTesting(null);
        }
    }

    private static Task Test1_HwidResolutionAcrossSlotChange() => RunIsolatedAsync(async mock =>
    {
        const string kbHwid = @"HID\VID_1234&PID_5678&MI_00";
        mock.SetHardwareId(2, kbHwid);

        var devices1 = InterceptionDriverService.Instance.EnumerateDevices();
        Assert.Equal(1, devices1.Count, "initial enumerated device count");
        Assert.Equal(2, InterceptionDriverService.Instance.ResolveDeviceSlot("keyboard", "VID_1234&PID_5678&MI_00", 1, 0), "full hwid filter");
        Assert.Equal(2, InterceptionDriverService.Instance.ResolveDeviceSlot("keyboard", "VID_1234&PID_5678", 1, 0), "initial slot");

        // Move keyboard from USB slot 2 to slot 7 with the same HWID
        mock.SetHardwareId(2, string.Empty);
        mock.SetHardwareId(7, kbHwid);

        var devices2 = InterceptionDriverService.Instance.EnumerateDevices();
        Assert.Equal(1, devices2.Count, "moved device count");
        Assert.Equal(7, InterceptionDriverService.Instance.ResolveDeviceSlot("keyboard", "VID_1234&PID_5678", 1, 0), "resolved moved slot");

        // Also verify InterceptionDeviceNode hooks slot 7 when deployed
        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [TestHarness.Node("dev", "interception.device",
                ("active", true), ("deviceKind", "keyboard"), ("hardwareIdFilter", "VID_1234&PID_5678"))],
            []);
        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(() => mock.GetSlotFilter(7) == InterceptionConstants.FilterKeyAll,
            failMessage: "Expected slot 7 to be hooked after HWID move.");

        var widget = TestHarness.GetWidgetJson(engine, "dev");
        Assert.True(widget.HasValue, "device widget state missing");
        Assert.Equal(7, widget!.Value.GetProperty("resolvedSlot").GetInt32(), "widget resolvedSlot");
    });

    private static Task Test2_ChordSuppressionAndReplacement() => RunIsolatedAsync(async mock =>
    {
        mock.SetHardwareId(1, @"HID\VID_1111&PID_2222&MI_00");

        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("kb", "interception.device",
                    ("active", true), ("deviceKind", "keyboard"), ("hardwareIdFilter", "VID_1111"),
                    ("defaultAction", "pass"), ("decisionTimeoutMs", 500)),
                TestHarness.Node("chord", "interception.chord-matcher",
                    ("modifierKey", "LWin"), ("triggerKey", "D"),
                    ("suppressModifier", true), ("suppressTrigger", true),
                    ("sendAction", "LCtrl+X"), ("chordWindowMs", 500)),
            ],
            [
                TestHarness.Edge("kb", "out", "chord", "in"),
                TestHarness.Edge("chord", "matched", "kb", "return"),
                TestHarness.Edge("chord", "other", "kb", "return"),
            ]);

        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(() => mock.GetSlotFilter(1) == InterceptionConstants.FilterKeyAll,
            failMessage: "Keyboard slot 1 filter not applied.");

        // 1. LWin down -> SUPPRESSED
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("LWin", isDown: true));
        Assert.Equal(0, mock.SnapshotSent().Count, "LWin down should be suppressed");

        // 2. D down -> SUPPRESSED, LCtrl+X injected
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("D", isDown: true));
        var sentAfterChord = mock.SnapshotSent().Select(s => (s.Device, Desc: DescribeKeyStroke(s.Stroke.Key))).ToArray();
        Assert.Equal(4, sentAfterChord.Length, "Expected 4 injected strokes for LCtrl+X");
        Assert.Equal("LCtrl:down", sentAfterChord[0].Desc, "step 0");
        Assert.Equal("X:down", sentAfterChord[1].Desc, "step 1");
        Assert.Equal("X:up", sentAfterChord[2].Desc, "step 2");
        Assert.Equal("LCtrl:up", sentAfterChord[3].Desc, "step 3");
        Assert.True(sentAfterChord.All(s => s.Device == 1), "All replacement keys must target slot 1");

        // Release D and LWin -> both suppressed
        mock.ClearSent();
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("D", isDown: false));
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("LWin", isDown: false));
        Assert.Equal(0, mock.SnapshotSent().Count, "Chord release strokes should be suppressed");

        // 3. Unrelated key A down -> passes through via other -> return
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("A", isDown: true));
        var sentA = mock.SnapshotSent();
        Assert.Equal(1, sentA.Count, "Unrelated key A should pass through");
        Assert.Equal(1, sentA[0].Device, "A device slot");
        Assert.Equal("A:down", DescribeKeyStroke(sentA[0].Stroke.Key), "A stroke unchanged");
    });

    private static Task Test3_MouseButtonToKeyboardShortcut() => RunIsolatedAsync(async mock =>
    {
        mock.SetHardwareId(3, @"HID\VID_AAAA&PID_BBBB&MI_00");
        mock.SetHardwareId(12, @"HID\VID_AAAA&PID_BBBB&MI_01");

        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("mouse", "interception.device",
                    ("active", true), ("deviceKind", "mouse"), ("hardwareIdFilter", "VID_AAAA&PID_BBBB&MI_01"),
                    ("defaultAction", "pass"), ("decisionTimeoutMs", 500)),
                TestHarness.Node("action", "interception.stroke-action",
                    ("matchKey", "X1"), ("matchPhase", "down"),
                    ("suppress", "suppress"), ("sendAction", "LCtrl+C"), ("sendMode", "tapOnDown")),
            ],
            [
                TestHarness.Edge("mouse", "out", "action", "in"),
                TestHarness.Edge("action", "matched", "mouse", "return"),
                TestHarness.Edge("action", "unmatched", "mouse", "return"),
            ]);

        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(() => mock.GetSlotFilter(12) == InterceptionConstants.FilterMouseButtonsAndWheel,
            failMessage: "Mouse slot 12 filter not applied.");

        await mock.InjectAndWaitAsync(12, MakeMouseStroke(InterceptionConstants.MouseButton4Down));
        var sent = mock.SnapshotSent();
        Assert.Equal(4, sent.Count, "Expected 4 injected key strokes for LCtrl+C and suppressed X1");
        Assert.True(sent.All(s => s.Device == 3), "Replacement LCtrl+C must be injected on keyboard slot 3");
        Assert.Equal("LCtrl:down", DescribeKeyStroke(sent[0].Stroke.Key));
        Assert.Equal("C:down", DescribeKeyStroke(sent[1].Stroke.Key));
        Assert.Equal("C:up", DescribeKeyStroke(sent[2].Stroke.Key));
        Assert.Equal("LCtrl:up", DescribeKeyStroke(sent[3].Stroke.Key));
    });

    private static Task Test4_FailSafeTimeoutForwardsStroke() => RunIsolatedAsync(async mock =>
    {
        mock.SetHardwareId(1, @"HID\VID_2000&PID_0001");

        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("kb", "interception.device",
                    ("active", true), ("deviceKind", "keyboard"), ("hardwareIdFilter", "*"),
                    ("defaultAction", "pass"), ("decisionTimeoutMs", 30)),
            ],
            []);

        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(() => mock.GetSlotFilter(1) == InterceptionConstants.FilterKeyAll,
            failMessage: "Slot 1 not hooked.");

        await mock.InjectAndWaitAsync(1, MakeKeyStroke("F", isDown: true));
        var sent = mock.SnapshotSent();
        Assert.Equal(1, sent.Count, "Unconnected device must forward stroke after timeout");
        Assert.Equal("F:down", DescribeKeyStroke(sent[0].Stroke.Key), "Forwarded stroke must match original");
    });

    private static Task Test5_EmergencyPanicKillSwitch() => RunIsolatedAsync(async mock =>
    {
        mock.SetHardwareId(1, @"HID\VID_3000&PID_0001");

        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("kb", "interception.device",
                    ("active", true), ("deviceKind", "keyboard"), ("hardwareIdFilter", "*"),
                    ("defaultAction", "suppress"), ("decisionTimeoutMs", 40)),
            ],
            []);

        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(() => mock.GetSlotFilter(1) == InterceptionConstants.FilterKeyAll,
            failMessage: "Slot 1 filter not applied before panic test.");

        await mock.InjectAndWaitAsync(1, MakeKeyStroke("LCtrl", isDown: true));
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("LAlt", isDown: true));
        await mock.InjectAndWaitAsync(1, MakeKeyStroke("Pause", isDown: true));

        Assert.True(InterceptionDriverService.Instance.IsPanicTriggered, "Panic flag must be true after LCtrl+LAlt+Pause");
        var filters = mock.SnapshotFilters();
        for (var slot = 1; slot <= InterceptionConstants.MaxDevice; slot++)
        {
            Assert.Equal((ushort)0, filters[slot], $"Slot {slot} filter must be cleared to 0 after panic");
        }
    });

    private static Task Test6_InterceptionHubMultiPortRouting() => RunIsolatedAsync(async mock =>
    {
        mock.SetHardwareId(2, @"HID\HUB_KB_ALPHA");
        mock.SetHardwareId(13, @"HID\HUB_MOUSE_BETA");

        await using var engine = TestHarness.CreateEngine();
        var ws = TestHarness.Workspace(
            [
                TestHarness.Node("hub", "interception.hub",
                    ("active", true), ("kb1Filter", "HUB_KB_ALPHA"), ("mouse1Filter", "HUB_MOUSE_BETA"),
                    ("defaultAction", "pass"), ("decisionTimeoutMs", 500)),
                TestHarness.Node("kbAct", "interception.stroke-action",
                    ("matchKey", "B"), ("matchPhase", "down"), ("suppress", "suppress"), ("sendAction", "Z")),
                TestHarness.Node("mAct", "interception.stroke-action",
                    ("matchKey", "X2"), ("matchPhase", "down"), ("suppress", "suppress"), ("sendAction", "Right")),
                TestHarness.Node("dbgKb", "basic.debug", ("label", "kb1")),
                TestHarness.Node("dbgMouse", "basic.debug", ("label", "mouse1")),
            ],
            [
                TestHarness.Edge("hub", "kb1", "kbAct", "in"),
                TestHarness.Edge("hub", "kb1", "dbgKb", "in"),
                TestHarness.Edge("kbAct", "matched", "hub", "return"),
                TestHarness.Edge("kbAct", "unmatched", "hub", "return"),

                TestHarness.Edge("hub", "mouse1", "mAct", "in"),
                TestHarness.Edge("hub", "mouse1", "dbgMouse", "in"),
                TestHarness.Edge("mAct", "matched", "hub", "return"),
                TestHarness.Edge("mAct", "unmatched", "hub", "return"),
            ]);

        await engine.DeployAsync(ws);
        await TestHarness.WaitUntilAsync(
            () => mock.GetSlotFilter(2) == InterceptionConstants.FilterKeyAll &&
                  mock.GetSlotFilter(13) == InterceptionConstants.FilterMouseButtonsAndWheel,
            failMessage: "Hub filters for kb1 (slot 2) and mouse1 (slot 13) were not applied.");

        // Keyboard B down on slot 2 -> suppressed, replaced by Z down+up
        await mock.InjectAndWaitAsync(2, MakeKeyStroke("B", isDown: true));
        var kbSent = mock.SnapshotSent();
        Assert.Equal(2, kbSent.Count, "Expected Z down+up from kb1");
        Assert.Equal(2, kbSent[0].Device);
        Assert.Equal("Z:down", DescribeKeyStroke(kbSent[0].Stroke.Key));
        Assert.Equal("Z:up", DescribeKeyStroke(kbSent[1].Stroke.Key));

        // Mouse X2 down on slot 13 -> suppressed, replaced by Right down+up
        mock.ClearSent();
        await mock.InjectAndWaitAsync(13, MakeMouseStroke(InterceptionConstants.MouseButton5Down));
        var mouseSent = mock.SnapshotSent();
        Assert.Equal(2, mouseSent.Count, "Expected Right mouse down+up from mouse1");
        Assert.Equal(13, mouseSent[0].Device);
        Assert.Equal(InterceptionConstants.MouseRightDown, mouseSent[0].Stroke.Mouse.State);
        Assert.Equal(InterceptionConstants.MouseRightUp, mouseSent[1].Stroke.Mouse.State);

        await TestHarness.WaitUntilAsync(() => engine.GetDebugHistory().Count >= 2,
            failMessage: "Expected independent debug traces from kb1 and mouse1.");
        var history = engine.GetDebugHistory();
        Assert.True(history.Any(d => d.Label == "kb1" && d.Payload?.ToString() == "B"), "kb1 did not emit B");
        Assert.True(history.Any(d => d.Label == "mouse1" && d.Payload?.ToString() == "X2"), "mouse1 did not emit X2");
    });
}
