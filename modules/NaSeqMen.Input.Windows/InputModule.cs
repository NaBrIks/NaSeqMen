// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Threading.Channels;
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Input.Windows;

public sealed partial class InputModule : NaModule, IDiscoveryCapture
{
    private readonly Dictionary<string, ConnectionDefinition> _connections = [];
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<CapturedSample> _incoming = Channel.CreateBounded<CapturedSample>(new BoundedChannelOptions(1024)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private RawInputMonitor? _monitor;
    private Task? _pump;
    private long _dropped;
    private long _generation;
    private sealed record CapturedSample(RawSample Sample, DateTimeOffset Time, long Generation, bool DiscoveryOnly);

    public override ModuleDescriptor Descriptor { get; } = new("windows.input", M("module.windows.input"), "0.1.0",
        true, false, [], []);

    public override ValueTask InitializeAsync(IModuleHost host, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(host, cancellationToken);
        _pump = PumpAsync(_stop.Token);
        return ValueTask.CompletedTask;
    }

    public override async ValueTask ConnectAsync(ConnectionDefinition connection, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            lock (_gate) _connections[connection.Id] = connection;
            if (_monitor is not null) return;
            Interlocked.Increment(ref _generation);
            _monitor = new RawInputMonitor(Received, error =>
                Host.Trace(new(DateTimeOffset.UtcNow, "", "", "error", M("input.error", ("detail", FromException(error))))));
            try { await _monitor.StartAsync(cancellationToken); }
            catch { _monitor = null; lock (_gate) _connections.Remove(connection.Id); throw; }
        }
        finally { _lifecycle.Release(); }
    }

    public override async ValueTask DisconnectAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            bool empty;
            lock (_gate) { _connections.Remove(connectionId); empty = _connections.Count == 0; }
            if (empty && _monitor is not null)
            {
                await _monitor.DisposeAsync();
                _monitor = null;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public override ValueTask<IReadOnlyList<DeviceDescriptor>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = RawInputMonitor.EnumerateDevices();
        var capturable = GetCapturable(devices);
        return ValueTask.FromResult<IReadOnlyList<DeviceDescriptor>>(devices
            .Select(device =>
            {
                var identity = PnpDeviceIdentity.Find(device.Id);
                var label = device.Kind == "keyboard" ? M("device.keyboard") :
                    device.Kind == "mouse" ? M("device.mouse") : M("device.hid");
                return new DeviceDescriptor(device.Id, label, device.Kind, device.Id, capturable.Contains(device.Id))
                {
                    PhysicalDeviceId = identity?.ContainerId.ToString("D"),
                    PhysicalDeviceName = identity?.Name
                };
            }).ToArray());
    }

    private void Received(RawSample sample) => QueueSample(sample, IsCaptured(sample.DeviceId));

    private void QueueSample(RawSample sample, bool discoveryOnly)
    {
        // Never make the Windows message loop wait on routing, scripts or the editor.
        var capture = new CapturedSample(sample, DateTimeOffset.UtcNow, Interlocked.Read(ref _generation), discoveryOnly);
        if (!_incoming.Writer.TryWrite(capture))
        {
            Interlocked.Increment(ref _generation);
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var modifiers = new ModifierTracker();
            var discoveryModifiers = new ModifierTracker();
            long generation = -1;
            await foreach (var capture in _incoming.Reader.ReadAllAsync(cancellationToken))
            {
                if (capture.Generation != Interlocked.Read(ref _generation)) continue;
                if (generation != capture.Generation)
                {
                    generation = capture.Generation;
                    modifiers.Reset();
                    discoveryModifiers.Reset();
                }
                var sample = capture.Sample;
                var modifierState = (capture.DiscoveryOnly ? discoveryModifiers : modifiers).Observe(sample, out var repeat);
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped != 0) Host.Trace(new(DateTimeOffset.UtcNow, "", "", "error",
                    M("input.dropped", ("count", dropped))));
                ConnectionDefinition[] connections;
                lock (_gate) connections = _connections.Values.ToArray();
                foreach (var connection in connections)
                {
                    if (connection.Settings.TryGetValue("devicePath", out var selected) && selected.Length != 0 &&
                        !string.Equals(selected, sample.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sample.Control == "move" && (!connection.Settings.TryGetValue("includeMovement", out var movement) ||
                        !bool.TryParse(movement, out var enabled) || !enabled)) continue;
                    var control = sample.Kind == "keyboard"
                        ? "key:" + ((sample.Flags & 2) != 0 ? "E0:" : (sample.Flags & 4) != 0 ? "E1:" : "") + sample.ScanCode.ToString("X2")
                        : sample.Kind + ":" + sample.Control;
                    var value = sample.Phase == "down" ? "1" : sample.Phase == "up" ? "0" :
                        sample.Control == "move" ? $"{sample.X},{sample.Y}" : sample.Value.ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        var signal = new Signal
                        {
                            Module = "windows.input", ConnectionId = connection.Id, Source = sample.DeviceId,
                            Control = control, Phase = sample.Phase, Time = capture.Time,
                            Modifiers = modifierState, IsRepeat = repeat, InputGeneration = generation,
                            ValueType = sample.Control == "move" || sample.Kind == "device" ? "text" : "number", Value = value
                        };
                        if (capture.DiscoveryOnly)
                            Host.Trace(new(capture.Time, signal.Id, "", "input", M("input.captured"), signal));
                        else await Host.PublishAsync(signal, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                    catch (Exception error) { Host.Trace(new(DateTimeOffset.UtcNow, "", "", "error", FromException(error))); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseDiscoveryAsync();
        if (_monitor is not null) await _monitor.DisposeAsync();
        _monitor = null;
        _stop.Cancel();
        _incoming.Writer.TryComplete();
        if (_pump is not null) await _pump;
        _stop.Dispose();
        _lifecycle.Dispose();
    }
}
