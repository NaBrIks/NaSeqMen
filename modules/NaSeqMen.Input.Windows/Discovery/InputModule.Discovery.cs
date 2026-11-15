// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Input.Windows;

public sealed partial class InputModule
{
    private readonly SemaphoreSlim _captureGate = new(1);
    private DiscoveryLease? _discovery;
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
                if (active?.Owner == owner) await ReleaseDiscoveryAsync(active);
                if (previous?.Owner == owner) Volatile.Write(ref _inspection, null);
                return [];
            }
            if (previous is not null && previous.Until > Environment.TickCount64 && previous.Owner != owner)
                throw Error<InvalidOperationException>(M("input.captureBusy"));
            var library = DiscoveryLibrary();
            var sources = interfaces.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var unchanged = previous is not null && previous.Until > Environment.TickCount64 && previous.Owner == owner &&
                previous.Library == library && previous.Sources.SequenceEqual(sources, StringComparer.OrdinalIgnoreCase);
            Volatile.Write(ref _inspection, new(owner, sources, library, Environment.TickCount64 + 2500));
            if (unchanged)
            {
                if (active?.Running != true) return [];
                active.Renew();
                return active.Sources;
            }
            if (active is not null) await ReleaseDiscoveryAsync(active);
            Interlocked.Increment(ref _generation);
            if (library is null) return [];
            var capturable = GetCapturable(RawInputMonitor.EnumerateDevices());
            var selected = sources.Where(capturable.Contains).ToArray();
            if (selected.Length == 0) return [];
            try
            {
                var discovery = new DiscoveryLease(owner, library, selected, sample => QueueSample(sample, discoveryOnly: true),
                    ReportCaptureError);
                Volatile.Write(ref _discovery, discovery);
                return discovery.Sources;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or DllNotFoundException or
                BadImageFormatException or EntryPointNotFoundException)
            {
                ReportCaptureError(error);
                return [];
            }
        }
        finally { _captureGate.Release(); }
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
            Volatile.Write(ref _inspection, null);
        }
        finally { _captureGate.Release(); }
    }

    private void ReportCaptureError(Exception error) => Host.Trace(new(DateTimeOffset.UtcNow, "", "", "error",
        M("input.error", ("detail", FromException(error)))));
}
