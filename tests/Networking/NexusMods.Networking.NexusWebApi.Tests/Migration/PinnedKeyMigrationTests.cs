using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Networking.NexusWebApi.Migration;
using NexusMods.Sdk;
using NexusMods.Secrets;

namespace NexusMods.Networking.NexusWebApi.Tests.Migration;

/// <summary>
/// A key pinned by NEXUS_API_KEY must still be moved: the migration writes through the store under the
/// overlay, so the stored copy is there the day the variable is unset.
/// </summary>
[Collection(LegacyCredentialCollection.Name)]
public class PinnedKeyMigrationTests : IAsyncLifetime
{
    private readonly IConnection _conn;

    public PinnedKeyMigrationTests(IConnection conn)
    {
        _conn = conn;
    }

    public async ValueTask InitializeAsync() => await LegacyCredentialSupport.Clear(_conn);

    public async ValueTask DisposeAsync() => await LegacyCredentialSupport.Clear(_conn);

    [Fact]
    public async Task AKeyThePinnedVariableCoversIsStillWrittenToTheStoreAndTheRowIsExcised()
    {
        await LegacyCredentialSupport.AddKey(_conn, "legacy-key");
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IEnvironmentReader>(new FakeEnvironment((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned-key")))
            .AddSecretKeys(NexusSecretKeys.All)
            .AddSecrets(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive, Backend = SecretBackendChoice.Memory })
            .BuildServiceProvider();
        var persistent = provider.GetRequiredService<IPersistentSecretStore>();
        var overlay = provider.GetRequiredService<ISecretStore>();

        // The overlay refuses a write to a pinned key, which is why the migration is handed the store under it.
        (await overlay.SetAsync(NexusSecretKeys.ApiKey, "x", CancellationToken.None)).Should().BeOfType<SecretWriteResult.ReadOnly>();

        var outcome = await new NexusApiKeyMigration(_conn, NullLogger<NexusApiKeyMigration>.Instance).RunAsync(persistent, CancellationToken.None);

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await persistent.GetAsync(NexusSecretKeys.ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("legacy-key"));
        (await overlay.GetAsync(NexusSecretKeys.ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("pinned-key"), "the variable still wins while it is set");
        ApiKey.All(_conn.Db).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().BeEmpty();
    }
}
