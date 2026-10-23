using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Networking.GOG.Migration;
using SilverModManager.Networking.GOG.Models.Legacy;

namespace SilverModManager.Networking.GOG.Tests.Migration;

/// <summary>
/// The tests share the one in-memory database, so they run one at a time and leave it empty.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LegacyGogAuthCollection
{
    public const string Name = "Legacy GOG auth rows";
}

[Collection(LegacyGogAuthCollection.Name)]
public class GogAuthMigrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset ExpiresAt = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private readonly IConnection _conn;
    private readonly InMemorySecretStore _store = new();

    public GogAuthMigrationTests(IConnection conn)
    {
        _conn = conn;
    }

    public async ValueTask InitializeAsync() => await Clear();

    public async ValueTask DisposeAsync() => await Clear();

    private async Task Clear()
    {
        var ids = Ids();
        if (ids.Length > 0) await _conn.Excise(ids);
    }

    private EntityId[] Ids() => _conn.History().Datoms(AuthInfo.AccessToken).Select(d => d.E).Distinct().ToArray();

    private async Task<EntityId> AddRow(string accessToken, string refreshToken, DateTimeOffset expiresAt, ulong userId = 99UL)
    {
        using var tx = _conn.BeginTransaction();
        var id = tx.TempId();
        tx.Add(id, AuthInfo.AccessToken, accessToken);
        tx.Add(id, AuthInfo.RefreshToken, refreshToken);
        tx.Add(id, AuthInfo.ExpiresAt, expiresAt);
        tx.Add(id, AuthInfo.SessionId, "legacy-session");
        tx.Add(id, AuthInfo.UserId, userId);
        var result = await tx.Commit();
        return result[id];
    }

    private async Task Retract(EntityId id)
    {
        using var tx = _conn.BeginTransaction();
        tx.Delete(id, false);
        await tx.Commit();
    }

    private Task<SecretMigrationOutcome> Run(ISecretStore? store = null)
        => new GogAuthMigration(_conn, NullLogger<GogAuthMigration>.Instance).RunAsync(store ?? _store, CancellationToken.None);

    private async Task<GogCredential?> Stored()
        => await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None) is SecretResult.Found found && GogCredential.TryParse(found.Value, out var credential) ? credential : null;

    [Fact]
    public async Task MovesALegacyRowIntoTheStoreAndExcisesIt()
    {
        await AddRow("old-access", "old-refresh", ExpiresAt);

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        (await Stored()).Should().Be(new GogCredential("old-access", "old-refresh", "legacy-session", 99UL, ExpiresAt));
        AuthInfo.All(_conn.Db).Should().BeEmpty();
        Ids().Should().BeEmpty();
    }

    [Fact]
    public async Task ARerunHasNothingToMove()
    {
        await AddRow("old-access", "old-refresh", ExpiresAt);
        await Run();

        (await Run()).Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Fact]
    public async Task AnUnavailableStoreDefersAndLeavesTheRowIntact()
    {
        await AddRow("old-access", "old-refresh", ExpiresAt);
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Deferred>().Which.Reason.Should().Contain("no session bus");
        AuthInfo.All(_conn.Db).Should().ContainSingle().Which.AccessToken.Should().Be("old-access");
    }

    [Fact]
    public async Task TheNewestOfTwoRowsWinsAndBothAreExcised()
    {
        await AddRow("older-access", "older-refresh", ExpiresAt);
        await AddRow("newer-access", "newer-refresh", ExpiresAt.AddHours(1));

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        (await Stored())!.AccessToken.Should().Be("newer-access");
        Ids().Should().BeEmpty();
    }

    [Fact]
    public async Task ARetractedRowThatWasNeverExcisedIsExcisedWithoutStoringAnything()
    {
        var id = await AddRow("old-access", "old-refresh", ExpiresAt);
        await Retract(id);
        AuthInfo.All(_conn.Db).Should().BeEmpty();
        Ids().Should().NotBeEmpty();

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        Ids().Should().BeEmpty();
        (await Stored()).Should().BeNull();
        _store.Calls.Should().NotContain(call => call.StartsWith("Set "));
    }

    [Fact]
    public void HasAStableName() => new GogAuthMigration(_conn, NullLogger<GogAuthMigration>.Instance).Name.Should().Be("gog-oauth");
}
