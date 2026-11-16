// SPDX-License-Identifier: MIT
namespace NaSeqMen.Contracts;

public interface IDiscoveryCapture
{
    ValueTask<string[]> SetDiscoveryAsync(Guid owner, string[] interfaces, CancellationToken cancellationToken);
}

public sealed record DiscoveryCaptureRequest(Guid Owner, string[] Interfaces)
{
    public const string Keyboard = "@discovery-keyboard";
    public const int LeaseMilliseconds = 2500;
    public bool AllowEditing { get; init; }
}
