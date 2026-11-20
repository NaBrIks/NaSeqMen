// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;
using static NaSeqMen.Contracts.MessageCatalog;

namespace NaSeqMen.Runtime;

public sealed partial class GraphRuntime
{
    private readonly SemaphoreSlim _discoveryChanges = new(1, 1);
    private DiscoverySession? _discoverySession;
    private long _discoveryVersion;
    private sealed record DiscoverySession(Guid Owner, long Until);

    private bool DiscoveryActive => Volatile.Read(ref _discoverySession) is { } session &&
        session.Until > Environment.TickCount64;

    private bool CanRoute(long version) => !DiscoveryActive && version == Interlocked.Read(ref _discoveryVersion);

    public async ValueTask<string[]> SetDiscoveryAsync(DiscoveryCaptureRequest request, CancellationToken cancellationToken)
    {
        await _discoveryChanges.WaitAsync(cancellationToken);
        try
        {
            var previous = Volatile.Read(ref _discoverySession);
            if (request.Interfaces.Length != 0)
            {
                var now = Environment.TickCount64;
                var wasActive = previous is not null && previous.Until > now;
                if (wasActive && previous!.Owner != request.Owner)
                    throw Error<InvalidOperationException>(M("input.captureBusy"));
                Volatile.Write(ref _discoverySession, new(request.Owner, now + DiscoveryCaptureRequest.LeaseMilliseconds));
                if (!wasActive)
                {
                    Interlocked.Increment(ref _discoveryVersion);
                    Interlocked.Increment(ref _queueEpoch);
                }
            }
            else if (previous?.Owner != request.Owner) return [];

            try
            {
                var active = new List<string>();
                var interfaces = request.AllowEditing ? [] : request.Interfaces;
                foreach (var module in _modules.Values.OfType<IDiscoveryCapture>())
                    active.AddRange(await module.SetDiscoveryAsync(request.Owner, interfaces, cancellationToken));
                return [.. active];
            }
            finally
            {
                if (request.Interfaces.Length == 0) Volatile.Write(ref _discoverySession, null);
            }
        }
        finally { _discoveryChanges.Release(); }
    }
}
