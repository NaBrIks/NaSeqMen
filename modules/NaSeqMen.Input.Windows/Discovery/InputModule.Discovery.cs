// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using System.ComponentModel;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Input.Windows;

public sealed partial class InputModule
{
    private readonly SemaphoreSlim _captureGate = new(1);
    private DiscoveryLease? _discovery;
    private KeyboardDiscoveryGuard? _keyboardGuard;
    private Inspection? _inspection;
    private sealed record Inspection(Guid Owner, string[] Sources, string? Library, long Until);

    public async ValueTask<string[]> SetDiscoveryAsync(Guid owner, string[] interfaces, CancellationToken cancellationToken)
    {
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            var active = Volatile.Read(ref _discovery);
            var previous = Volatile.Read(ref _inspection);
            if (interfaces.Length == 0)
            {
                if (previous?.Owner == owner)
                {
                    await ReleaseKeyboardGuardAsync();
                    if (active?.Owner == owner) await ReleaseDiscoveryAsync(active);
                    Volatile.Write(ref _inspection, null);
                    Interlocked.Increment(ref _generation);
                }
                return [];
            }
            if (previous is not null && previous.Until > Environment.TickCount64 && previous.Owner != owner)
                throw Error<InvalidOperationException>(M("input.captureBusy"));
            var library = DiscoveryLibrary();
            var sources = interfaces.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var unchanged = previous is not null && previous.Until > Environment.TickCount64 && previous.Owner == owner &&
                previous.Library == library && previous.Sources.SequenceEqual(sources, StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref _inspection, new(owner, sources, library, Environment.TickCount64 + DiscoveryCaptureRequest.LeaseMilliseconds));
            var keyboard = await EnsureKeyboardGuardAsync();
            string[] Result(string[] captured) => keyboard ? [DiscoveryCaptureRequest.Keyboard, .. captured] : captured;
            if (unchanged)
            {
                if (active?.Running != true) return Result([]);
                active.Renew();
                return Result(active.Sources);
            }
            if (active is not null) await ReleaseDiscoveryAsync(active);
            Interlocked.Increment(ref _generation);
            if (library is null) return Result([]);
            var capturable = GetCapturable(RawInputMonitor.EnumerateDevices());
            var selected = sources.Where(capturable.Contains).ToArray();
            if (selected.Length == 0) return Result([]);
            try
            {
                var discovery = new DiscoveryLease(owner, library, selected, sample => QueueSample(sample, discoveryOnly: true),
                    ReportCaptureError);
                Volatile.Write(ref _discovery, discovery);
                return Result(discovery.Sources);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or DllNotFoundException or
                BadImageFormatException or EntryPointNotFoundException)
            {
                ReportCaptureError(error);
                return Result([]);
            }
        }
        finally { _captureGate.Release(); }
    }

    private async Task<bool> EnsureKeyboardGuardAsync()
    {
        if (_keyboardGuard?.Renew() == true) return true;
        if (_keyboardGuard is { HasForeground: false }) return false;
        await ReleaseKeyboardGuardAsync();
        var foreground = KeyboardDiscoveryGuard.ForegroundEditor();
        if (foreground == 0) return false;
        var guard = new KeyboardDiscoveryGuard(foreground, sample => QueueSample(sample, discoveryOnly: true));
        try
        {
            await guard.StartAsync();
            Volatile.Write(ref _keyboardGuard, guard);
            return guard.Filtering;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or TimeoutException)
        {
            await guard.DisposeAsync();
            ReportCaptureError(error);
            return false;
        }
    }

    private async ValueTask ReleaseKeyboardGuardAsync()
    {
        var guard = Interlocked.Exchange(ref _keyboardGuard, null);
        if (guard is not null) await guard.DisposeAsync();
    }

    private bool IsCaptured(string source)
    {
        var inspection = Volatile.Read(ref _inspection);
        return inspection is not null && inspection.Until > Environment.TickCount64 &&
            inspection.Sources.Contains(source, StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> GetCapturable(IReadOnlyList<RawDevice> devices)
    {
        var library = DiscoveryLibrary();
        if (library is null) return [];
        try
        {
            using var backend = new InterceptionApi(library);
            return new(backend.Resolve(devices).Keys, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { return []; }
    }

    private string? DiscoveryLibrary()
    {
        lock (_gate)
        {
            var paths = _connections.Values.Select(connection => connection.Settings.GetValueOrDefault("captureLibrary", ""))
                .Where(path => Path.IsPathFullyQualified(path) && File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return paths.Length == 1 ? paths[0] : null;
        }
    }

    private async ValueTask ReleaseDiscoveryAsync(DiscoveryLease active)
    {
        await active.DisposeAsync();
        Volatile.Write(ref _discovery, null);
        Interlocked.Increment(ref _generation);
    }

    private async ValueTask CloseDiscoveryAsync()
    {
        await _captureGate.WaitAsync();
        try
        {
            if (_discovery is not null) await ReleaseDiscoveryAsync(_discovery);
            await ReleaseKeyboardGuardAsync();
            Volatile.Write(ref _inspection, null);
        }
        finally { _captureGate.Release(); }
    }

    private void ReportCaptureError(Exception error) => Host.Trace(new(DateTimeOffset.UtcNow, "", "", "error",
        M("input.error", ("detail", FromException(error)))));
}
