// SPDX-License-Identifier: MIT
using NaSeqMen.Contracts;

namespace NaSeqMen.Runtime;

public sealed partial class GraphRuntime
{
    public async ValueTask<string[]> SetDiscoveryAsync(DiscoveryCaptureRequest request, CancellationToken cancellationToken)
    {
        var active = new List<string>();
        foreach (var module in _modules.Values.OfType<IDiscoveryCapture>())
            active.AddRange(await module.SetDiscoveryAsync(request.Owner, request.Interfaces, cancellationToken));
        return [.. active];
    }
}
