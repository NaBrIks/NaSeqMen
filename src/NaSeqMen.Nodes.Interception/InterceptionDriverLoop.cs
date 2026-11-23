namespace NaSeqMen.Nodes.Interception;

/// <summary>
/// Background driver loop, selective per-slot filtering, loop-back wait, and direct injection logic.
/// </summary>
public sealed partial class InterceptionDriverService
{
    private void EnsureWorkerRunningLocked()
    {
        if (_workerThread is not null && _workerThread.IsAlive)
            return;

        if (_backend is null || !_backend.IsAvailable)
            return;

        _workerCts = new CancellationTokenSource();
        var ct = _workerCts.Token;
        var backend = _backend;

        _workerThread = new Thread(() => DriverLoop(backend, ct))
        {
            IsBackground = true,
            Name = "NaSeqMen.Interception.DriverLoop",
            Priority = ThreadPriority.AboveNormal,
        };
        _workerThread.Start();
    }

    private void StopWorkerLocked()
    {
        var cts = _workerCts;
        var thread = _workerThread;
        _workerCts = null;
        _workerThread = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }

        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            Monitor.Exit(_gate);
            try { thread.Join(TimeSpan.FromSeconds(2)); }
            finally { Monitor.Enter(_gate); }
        }

        cts?.Dispose();
    }

    private void DriverLoop(IInterceptionBackend backend, CancellationToken ct)
    {
        var ctx = backend.CreateContext();
        if (ctx == nint.Zero)
            return;

        lock (_gate)
        {
            _activeContext = ctx;
            _filtersDirty = true;
        }

        _lCtrlDown = _rCtrlDown = _lAltDown = _rAltDown = false;

        try
        {
            var stroke = default(InterceptionStroke);
            while (!ct.IsCancellationRequested)
            {
                if (_filtersDirty)
                {
                    lock (_gate)
                    {
                        ApplyFiltersLocked(backend, ctx);
                        _filtersDirty = false;
                    }
                }

                var device = backend.WaitWithTimeout(ctx, WaitTimeoutMs);
                if (device <= 0 || device > InterceptionConstants.MaxDevice)
                    continue;

                while (!ct.IsCancellationRequested && backend.Receive(ctx, device, ref stroke) > 0)
                {
                    ProcessStroke(backend, ctx, device, in stroke, ct);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                ClearAllFiltersLocked(backend, ctx);
                if (_activeContext == ctx)
                    _activeContext = nint.Zero;
            }

            backend.DestroyContext(ctx);
        }
    }

    private void ApplyFiltersLocked(IInterceptionBackend backend, nint ctx)
    {
        var desired = new ushort[InterceptionConstants.MaxDevice + 1];

        if (!_panicTriggered)
        {
            foreach (var sub in _subscriptions)
            {
                if (sub.Config.MatchAllDevices)
                {
                    var normKind = InterceptionStrokeHelper.NormalizeKind(sub.Config.DeviceKind);
                    foreach (var dev in _cachedDevices)
                    {
                        if (dev.Slot < 1 || dev.Slot > InterceptionConstants.MaxDevice)
                            continue;
                        if (normKind == "keyboard" && !InterceptionConstants.IsKeyboardSlot(dev.Slot))
                            continue;
                        if (normKind == "mouse" && !InterceptionConstants.IsMouseSlot(dev.Slot))
                            continue;

                        if (InterceptionConstants.IsKeyboardSlot(dev.Slot))
                            desired[dev.Slot] = InterceptionConstants.FilterKeyAll;
                        else if (InterceptionConstants.IsMouseSlot(dev.Slot))
                            desired[dev.Slot] |= sub.Config.CaptureMove
                                ? InterceptionConstants.FilterMouseAll
                                : InterceptionConstants.FilterMouseButtonsAndWheel;
                    }
                    continue;
                }

                var slot = sub.ResolvedSlot;
                if (slot < 1 || slot > InterceptionConstants.MaxDevice)
                    continue;

                if (InterceptionConstants.IsKeyboardSlot(slot))
                {
                    desired[slot] = InterceptionConstants.FilterKeyAll;
                }
                else if (InterceptionConstants.IsMouseSlot(slot))
                {
                    desired[slot] |= sub.Config.CaptureMove
                        ? InterceptionConstants.FilterMouseAll
                        : InterceptionConstants.FilterMouseButtonsAndWheel;
                }
            }
        }

        _activePredicates.Clear();

        var snapshot = (ushort[])desired.Clone();
        InterceptionPredicate nonePred = d => d >= 1 && d <= InterceptionConstants.MaxDevice && snapshot[d] == 0 ? 1 : 0;
        _activePredicates.Add(nonePred);
        backend.SetFilter(ctx, nonePred, 0x0000);

        var distinctFilters = new HashSet<ushort>();
        for (var i = 1; i <= InterceptionConstants.MaxDevice; i++)
        {
            if (snapshot[i] != 0)
                distinctFilters.Add(snapshot[i]);
        }

        foreach (var filterValue in distinctFilters)
        {
            var captured = filterValue;
            InterceptionPredicate pred = d =>
                d >= 1 && d <= InterceptionConstants.MaxDevice && snapshot[d] == captured ? 1 : 0;
            _activePredicates.Add(pred);
            backend.SetFilter(ctx, pred, captured);
        }
    }

    private void ClearAllFiltersLocked(IInterceptionBackend backend, nint ctx)
    {
        _activePredicates.Clear();
        _activePredicates.Add(_allDevicesPredicate);
        backend.SetFilter(ctx, _allDevicesPredicate, 0x0000);
    }

    private void ProcessStroke(
        IInterceptionBackend backend,
        nint ctx,
        int device,
        in InterceptionStroke stroke,
        CancellationToken ct)
    {
        var isKeyboard = InterceptionConstants.IsKeyboardSlot(device);

        if (isKeyboard && CheckEmergencyPanic(in stroke.Key))
        {
            EmergencyReleaseAllFilters();
            backend.Send(ctx, device, in stroke);
            return;
        }

        if (_panicTriggered)
        {
            backend.Send(ctx, device, in stroke);
            return;
        }

        List<ActiveSubscription> matching;
        InterceptionDeviceInfo? devInfo;
        lock (_gate)
        {
            matching = _subscriptions.Where(s =>
                s.ResolvedSlot == device ||
                (s.Config.MatchAllDevices && MatchesDeviceKind(s.Config.DeviceKind, isKeyboard))).ToList();
            devInfo = _cachedDevices.FirstOrDefault(d => d.Slot == device);
        }

        if (matching.Count == 0 ||
            (!isKeyboard && stroke.Mouse.State == 0 && !matching.Any(s => s.Config.CaptureMove)))
        {
            backend.Send(ctx, device, in stroke);
            return;
        }

        var originHwid = devInfo?.HardwareId ?? matching[0].ResolvedHardwareId;
        var originDisplayName = devInfo?.DisplayName ?? originHwid;
        var finalDecision = AwaitCombinedDecision(matching, device, originHwid, originDisplayName, isKeyboard, in stroke, ct);

        if (!finalDecision.Suppress)
            backend.Send(ctx, device, in stroke);

        if (!finalDecision.BlockActions &&
            (finalDecision.SendKeys is { Count: > 0 } || finalDecision.SendMouse is { Count: > 0 }))
        {
            var devices = _cachedDevices.Count > 0 ? _cachedDevices : EnumerateDevices();
            InterceptionStrokeHelper.SendDecisionActions(
                backend, ctx, finalDecision, device, originHwid, devices, ResolveDeviceSlot);
        }
    }

    private StrokeDecision AwaitCombinedDecision(
        IReadOnlyList<ActiveSubscription> matching,
        int device,
        string hardwareId,
        string? displayName,
        bool isKeyboard,
        in InterceptionStroke stroke,
        CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        var pending = new PendingDecision();
        _pendingDecisions[correlationId] = pending;

        var defaultSuppress = matching.Any(s =>
            string.Equals(s.Config.DefaultAction, "suppress", StringComparison.OrdinalIgnoreCase));
        var timeoutMs = Math.Clamp(matching.Max(s => s.Config.DecisionTimeoutMs), 5, 2000);

        var tasks = new List<Task>(matching.Count);
        foreach (var sub in matching)
        {
            if (!isKeyboard && stroke.Mouse.State == 0 && !sub.Config.CaptureMove)
                continue;

            var basket = InterceptionStrokeHelper.BuildStrokeMessage(
                sub, correlationId, device, hardwareId, displayName, isKeyboard, in stroke);
            var capturedSub = sub;
            tasks.Add(Task.Run(async () =>
            {
                try { await capturedSub.OnStroke(basket).ConfigureAwait(false); }
                catch { /* isolate downstream faults from the driver loop */ }
            }, CancellationToken.None));
        }

        try
        {
            if (tasks.Count > 0)
            {
                Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).GetAwaiter().GetResult();
            }
            return pending.BuildFinal(defaultSuppress);
        }
        catch (TimeoutException)
        {
            return pending.BuildFinal(defaultSuppress);
        }
        catch (OperationCanceledException)
        {
            return new StrokeDecision(defaultSuppress);
        }
        finally
        {
            _pendingDecisions.TryRemove(correlationId, out _);
        }
    }

    private static bool MatchesDeviceKind(string configuredKind, bool isKeyboard)
    {
        var norm = InterceptionStrokeHelper.NormalizeKind(configuredKind);
        if (norm == "keyboard") return isKeyboard;
        if (norm == "mouse") return !isKeyboard;
        return true; // "any" / "all" / empty
    }

    private bool CheckEmergencyPanic(in InterceptionKeyStroke key)
    {
        var isDown = (key.State & InterceptionConstants.KeyUp) == 0;
        var keyName = ScanCodeMap.GetKeyNameFromState(key.Code, key.State);

        switch (keyName)
        {
            case "LCtrl": _lCtrlDown = isDown; break;
            case "RCtrl": _rCtrlDown = isDown; break;
            case "LAlt": _lAltDown = isDown; break;
            case "RAlt": _rAltDown = isDown; break;
            case "Pause" when isDown && (_lCtrlDown || _rCtrlDown) && (_lAltDown || _rAltDown):
                return true;
        }

        return false;
    }

    private bool InjectDirect(StrokeDecision decision, string? dllPath)
    {
        IInterceptionBackend backend;
        nint ctx;
        var createdTemp = false;

        lock (_gate)
        {
            backend = EnsureBackendLocked(dllPath);
            if (!backend.IsAvailable)
                return false;

            ctx = _activeContext;
            if (ctx == nint.Zero)
            {
                ctx = backend.CreateContext();
                createdTemp = true;
            }
        }

        if (ctx == nint.Zero)
            return false;

        try
        {
            var devices = _cachedDevices.Count > 0 ? _cachedDevices : EnumerateDevices(dllPath);
            InterceptionStrokeHelper.SendDecisionActions(
                backend, ctx, decision, originDevice: 0, originHardwareId: string.Empty, devices, ResolveDeviceSlot);
            return true;
        }
        finally
        {
            if (createdTemp)
                backend.DestroyContext(ctx);
        }
    }

    private void CancelAllPendingDecisions()
    {
        _pendingDecisions.Clear();
    }
}
