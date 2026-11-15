// SPDX-License-Identifier: MIT
namespace NaSeqMen.Contracts;

public interface IDiscoveryCapture
{
    ValueTask<string[]> SetDiscoveryAsync(Guid owner, string[] interfaces, CancellationToken cancellationToken);
}

public sealed record DiscoveryCaptureRequest(Guid Owner, string[] Interfaces);
