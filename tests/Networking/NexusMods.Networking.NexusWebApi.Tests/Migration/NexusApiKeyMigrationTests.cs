using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Networking.NexusWebApi.Migration;

namespace NexusMods.Networking.NexusWebApi.Tests.Migration;

[Collection(LegacyCredentialCollection.Name)]
public class NexusApiKeyMigrationTests : IAsyncLifetime
{
    private readonly IConnection _conn;
    private readonly InMemorySecretStore _store = new();

    public NexusApiKeyMigrationTests(IConnection conn)
    {
        _conn = conn;
    }

    public async ValueTask InitializeAsync() => await LegacyCredentialSupport.Clear(_conn);

    public async ValueTask DisposeAsync() => await LegacyCredentialSupport.Clear(_conn);

    private Task<SecretMigrationOutcome> Run(ISecretStore? store = null)
        => new NexusApiKeyMigration(_conn, NullLogger<NexusApiKeyMigration>.Instance).RunAsync(store ?? _store, CancellationToken.None);

    [Fact]
    public async Task MovesTheKeyIntoTheStoreAndExcisesTheRow()
    {
        await LegacyCredentialSupport.AddKey(_conn, "old-key");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await AuthTestSupport.ReadKey(_store)).Should().Be("old-key");
        ApiKey.All(_conn.Db).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().BeEmpty("excise removes the history too");
    }

    [Fact]
    public async Task ARerunHasNothingToMove()
    {
        await LegacyCredentialSupport.AddKey(_conn, "old-key");
        await Run();

        (await Run()).Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Fact]
    public async Task NothingToMoveWhenNoRowWasEverStored()
    {
        (await Run()).Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
        _store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnavailableStoreDefersAndLeavesTheRowIntact()
    {
        await LegacyCredentialSupport.AddKey(_conn, "old-key");
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Deferred>().Which.Reason.Should().Contain("wallet is locked");
        ApiKey.Get(_conn.Db).Should().Be("old-key");
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().HaveCount(1);
    }

    [Fact]
    public async Task ADeferredStepMovesTheKeyOnTheNextRun()
    {
        await LegacyCredentialSupport.AddKey(_conn, "old-key");
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        await Run();

        _store.Availability = null;

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await AuthTestSupport.ReadKey(_store)).Should().Be("old-key");
    }

    [Fact]
    public async Task TheNewestOfTwoRowsWinsAndBothAreExcised()
    {
        await LegacyCredentialSupport.AddKey(_conn, "older-key");
        await LegacyCredentialSupport.AddKey(_conn, "newer-key");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await AuthTestSupport.ReadKey(_store)).Should().Be("newer-key");
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().BeEmpty();
    }

    [Fact]
    public async Task ARetractedRowThatWasNeverExcisedIsFoundAndExcised()
    {
        var id = await LegacyCredentialSupport.AddKey(_conn, "old-key");
        await LegacyCredentialSupport.Retract(_conn, id);
        ApiKey.All(_conn.Db).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().NotBeEmpty("a retract alone leaves the value in history");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().BeEmpty();
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull("a retracted key was the user's logout, not a key to restore");
    }

    [Fact]
    public async Task ARetractedRowAndALiveRowLeaveOnlyTheLiveValueInTheStore()
    {
        var old = await LegacyCredentialSupport.AddKey(_conn, "old-key");
        await LegacyCredentialSupport.Retract(_conn, old);
        await LegacyCredentialSupport.AddKey(_conn, "live-key");

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        (await AuthTestSupport.ReadKey(_store)).Should().Be("live-key");
        LegacyCredentialSupport.Ids(_conn, ApiKey.Key).Should().BeEmpty();
    }

    [Fact]
    public void HasAStableName() => new NexusApiKeyMigration(_conn, NullLogger<NexusApiKeyMigration>.Instance).Name.Should().Be("nexus-api-key");
}
