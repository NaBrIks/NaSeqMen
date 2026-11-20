// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NaSeqMen.Core;
using NaSeqMen.Input.Windows;
using NaSeqMen.Interception;

internal sealed class PassiveInput : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Subscription, byte> _subscriptions = new();
    private RawInputMonitor? _monitor;

    internal async ValueTask<IAdapterSession?> SubscribeAsync(CaptureSettings settings, AdapterHost host, CancellationToken ct)
    {
        var subscription = new Subscription(this, settings.Devices, host);
        subscription.Refresh();
        if (_monitor is null)
        {
            var monitor = new RawInputMonitor(Received, Failed);
            await monitor.StartAsync(ct);
            _monitor = monitor;
        }
        _subscriptions.TryAdd(subscription, 0);
        return subscription;
    }

    private void Received(RawSample sample)
    {
        foreach (var subscription in _subscriptions.Keys)
            if (sample.Kind == "device")
            {
                try { subscription.Refresh(); }
                catch (Exception error) { subscription.Host.Error(error); }
            }
            else subscription.Receive(sample);
    }

    private void Failed(Exception error)
    {
        foreach (var subscription in _subscriptions.Keys) subscription.Host.Error(error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_monitor is not null) await _monitor.DisposeAsync();
    }

    private sealed class Subscription(PassiveInput owner, DeviceSelection[] selections, AdapterHost host) : IAdapterSession
    {
        private sealed record Match(DeviceSelection Selection, RawDevice[] Candidates);
        private volatile Match[] _matches = [];
        private long _lastMove;
        internal AdapterHost Host => host;

        internal void Refresh()
        {
            var devices = RawInputMonitor.EnumerateDevices();
            _matches = selections.Select(selection => new Match(selection, devices.Where(device =>
                device.Kind == selection.Kind && string.Equals(HardwareKey(device.Id), Normalize(selection.HardwareId),
                    StringComparison.OrdinalIgnoreCase)).ToArray())).ToArray();
        }

        internal void Receive(RawSample sample)
        {
            var match = _matches.FirstOrDefault(x => x.Candidates.Length == 1 &&
                StringComparer.OrdinalIgnoreCase.Equals(x.Candidates[0].Id, sample.DeviceId));
            if (match is null) return;
            if (sample.Control == "move")
            {
                var now = Environment.TickCount64;
                if (now - _lastMove < 160) return;
                _lastMove = now;
            }
            _ = host.Publish(new("interception.capture", GraphJson.Value(new
            {
                device = match.Selection.Id, sample.Kind, sample.Control, sample.Phase,
                code = sample.ScanCode, sample.Flags, sample.Value, sample.X, sample.Y, transport = "rawInput"
            })));
        }

        public ValueTask<IReadOnlyList<Emission>> InvokeAsync(Node node, string port, Packet message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (node.Type != "interception.devices") throw new InvalidOperationException("Operation requires live Interception.");
            Refresh();
            return ValueTask.FromResult<IReadOnlyList<Emission>>([new("out", GraphJson.Value(new
            {
                transport = "rawInput",
                devices = _matches.Select(x => new { x.Selection.Id, x.Selection.Kind,
                    matches = x.Candidates.Length, status = x.Candidates.Length switch { 0 => "notFound", 1 => "observed", _ => "ambiguous" } })
            }))]);
        }

        public ValueTask DisposeAsync()
        {
            owner._subscriptions.TryRemove(this, out _);
            return ValueTask.CompletedTask;
        }

        private static string? HardwareKey(string path)
        {
            const string prefix = @"\\?\";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var parts = path[prefix.Length..].Split('#', 3);
            return parts.Length == 3 ? Normalize(parts[0] + "\\" + parts[1]) : null;
        }

        private static string Normalize(string id) => Regex.Replace(id, "&REV_[0-9A-F]+", "", RegexOptions.IgnoreCase);
    }
}
