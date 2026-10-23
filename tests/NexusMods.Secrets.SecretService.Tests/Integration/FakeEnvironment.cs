using NexusMods.Sdk;

namespace NexusMods.Secrets.SecretService.Tests.Integration;

internal sealed class FakeEnvironment(string? busAddress) : IEnvironmentReader
{
    public string? Get(string name) => name == SecretServiceStore.BusAddressVariable ? busAddress : null;
}
