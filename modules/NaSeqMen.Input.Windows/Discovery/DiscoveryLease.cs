// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Input.Windows;

internal sealed class DiscoveryLease : IAsyncDisposable
{
    private const long LeaseMs = 2500;
    private const ushort OtherMouseButtons = 0x0FFC;
    private readonly InterceptionApi _api;
    private readonly Dictionary<int, string> _interfaces;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private long _expires = Environment.TickCount64 + LeaseMs;

    public Guid Owner { get; }
    public string[] Sources { get; }
    public string Library { get; }
    public bool Running => !_worker.IsCompleted;

    public DiscoveryLease(Guid owner, string library, string[] sources, Action<RawSample> capture, Action<Exception> report)
    {
        Owner = owner;
        Library = library;
        Sources = sources;
        _api = new InterceptionApi(library);
        _interfaces = [];
        try
        {
            var available = _api.Resolve(RawInputMonitor.EnumerateDevices());
            if (sources.Any(source => !available.ContainsKey(source)))
                throw Error<InvalidOperationException>(M("input.captureUnavailable"));
            foreach (var source in sources) _interfaces.Add(available[source], source);
            foreach (var index in _interfaces.Keys)
            {
                if (!_api.SetFilter(index, index <= 10 ? ushort.MaxValue : OtherMouseButtons))
                    throw Error<InvalidOperationException>(M("input.captureUnavailable"));
            }
            _worker = Task.Factory.StartNew(() => Run(capture, report), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            ReleaseFilters();
            _stop.Dispose();
            throw;
        }
    }

    public void Renew() => Interlocked.Exchange(ref _expires, Environment.TickCount64 + LeaseMs);

    private void Run(Action<RawSample> capture, Action<Exception> report)
    {
        var stroke = new byte[20];
        var held = new HashSet<(int Device, ushort Scan, int Extended)>();
        try
        {
            while (!_stop.IsCancellationRequested && Environment.TickCount64 < Interlocked.Read(ref _expires))
            {
                var index = _api.WaitForDevice();
                if (index == 0 || !_api.Receive(index, stroke)) continue;
                if (!_interfaces.TryGetValue(index, out var source))
                {
                    _api.Send(index, stroke);
                    continue;
                }
                if (index <= 10)
                {
                    var scan = BinaryPrimitives.ReadUInt16LittleEndian(stroke);
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(stroke.AsSpan(2));
                    var key = (index, scan, flags & 6);
                    if ((flags & 1) == 0) held.Add(key);
                    else if (!held.Remove(key)) { _api.Send(index, stroke); continue; }
                    capture(new(source, "keyboard", "", (flags & 1) == 0 ? "down" : "up", ScanCode: scan, Flags: flags));
                }
                else CaptureMouse(index, source, stroke, capture);
            }
        }
        catch (Exception error) { report(error); }
        finally { ReleaseFilters(); }
    }

    private void CaptureMouse(int index, string source, byte[] stroke, Action<RawSample> capture)
    {
        var buttons = BinaryPrimitives.ReadUInt16LittleEndian(stroke);
        var wheel = BinaryPrimitives.ReadInt16LittleEndian(stroke.AsSpan(4));
        for (var button = 1; button < 5; button++)
        {
            if ((buttons & (1 << (button * 2))) != 0) capture(new(source, "mouse", $"button{button + 1}", "down"));
            if ((buttons & (2 << (button * 2))) != 0) capture(new(source, "mouse", $"button{button + 1}", "up"));
        }
        if ((buttons & 0x400) != 0) capture(new(source, "mouse", "wheel", "change", wheel));
        if ((buttons & 0x800) != 0) capture(new(source, "mouse", "horizontalWheel", "change", wheel));
        var primary = (ushort)(buttons & 3);
        BinaryPrimitives.WriteUInt16LittleEndian(stroke, primary);
        BinaryPrimitives.WriteInt16LittleEndian(stroke.AsSpan(4), 0);
        if (primary != 0 || BinaryPrimitives.ReadInt32LittleEndian(stroke.AsSpan(8)) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(stroke.AsSpan(12)) != 0) _api.Send(index, stroke);
    }

    private void ReleaseFilters()
    {
        foreach (var index in _interfaces.Keys) _api.SetFilter(index, 0);
        _api.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _worker;
        _stop.Dispose();
    }
}
