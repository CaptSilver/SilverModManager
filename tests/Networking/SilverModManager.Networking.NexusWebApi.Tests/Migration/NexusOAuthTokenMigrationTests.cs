using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Networking.NexusWebApi.Migration;

namespace NexusMods.Networking.NexusWebApi.Tests.Migration;

[Collection(LegacyCredentialCollection.Name)]
public class NexusOAuthTokenMigrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset ExpiresAt = new(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private readonly IConnection _conn;
    private readonly InMemorySecretStore _store = new();

    public NexusOAuthTokenMigrationTests(IConnection conn)
    {
        _conn = conn;
    }

    public async ValueTask InitializeAsync() => await LegacyCredentialSupport.Clear(_conn);

    public async ValueTask DisposeAsync() => await LegacyCredentialSupport.Clear(_conn);

    private Task<SecretMigrationOutcome> Run(ISecretStore? store = null)
        => new NexusOAuthTokenMigration(_conn, NullLogger<NexusOAuthTokenMigration>.Instance).RunAsync(store ?? _store, CancellationToken.None);

    [Fact]
    public async Task MovesTheTokenIntoTheStoreAndExcisesTheRow()
    {
        await LegacyCredentialSupport.AddToken(_conn, "old-access", "old-refresh", ExpiresAt);

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Moved>();
        (await AuthTestSupport.ReadToken(_store)).Should().Be(new NexusOAuthToken("old-access", "old-refresh", ExpiresAt));
        JWTToken.All(_conn.Db).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, JWTToken.AccessToken).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, JWTToken.RefreshToken).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, JWTToken.ExpiresAt).Should().BeEmpty();
    }

    [Fact]
    public async Task TheStoredValueIsTheDocumentedJson()
    {
        await LegacyCredentialSupport.AddToken(_conn, "old-access", "old-refresh", ExpiresAt);

        await Run();

        var found = (SecretResult.Found)await _store.GetAsync(NexusSecretKeys.OAuth, CancellationToken.None);
        using var document = JsonDocument.Parse(found.Value);
        document.RootElement.GetProperty("accessToken").GetString().Should().Be("old-access");
        document.RootElement.GetProperty("refreshToken").GetString().Should().Be("old-refresh");
        document.RootElement.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(ExpiresAt);
    }

    [Fact]
    public async Task ARerunHasNothingToMove()
    {
        await LegacyCredentialSupport.AddToken(_conn, "old-access", "old-refresh", ExpiresAt);
        await Run();

        (await Run()).Should().BeOfType<SecretMigrationOutcome.NothingToMove>();
    }

    [Fact]
    public async Task AnUnavailableStoreDefersAndLeavesTheRowIntact()
    {
        await LegacyCredentialSupport.AddToken(_conn, "old-access", "old-refresh", ExpiresAt);
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");

        var outcome = await Run();

        outcome.Should().BeOfType<SecretMigrationOutcome.Deferred>().Which.Reason.Should().Contain("no session bus");
        JWTToken.All(_conn.Db).Should().ContainSingle().Which.AccessToken.Should().Be("old-access");
    }

    [Fact]
    public async Task TheNewestOfTwoRowsWinsAndBothAreExcised()
    {
        await LegacyCredentialSupport.AddToken(_conn, "older-access", "older-refresh", ExpiresAt);
        await LegacyCredentialSupport.AddToken(_conn, "newer-access", "newer-refresh", ExpiresAt.AddHours(1));

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        (await AuthTestSupport.ReadToken(_store))!.AccessToken.Should().Be("newer-access");
        LegacyCredentialSupport.Ids(_conn, JWTToken.AccessToken).Should().BeEmpty();
    }

    [Fact]
    public async Task ARetractedRowThatWasNeverExcisedIsFoundAndExcised()
    {
        var id = await LegacyCredentialSupport.AddToken(_conn, "old-access", "old-refresh", ExpiresAt);
        await LegacyCredentialSupport.Retract(_conn, id);
        JWTToken.All(_conn.Db).Should().BeEmpty();
        LegacyCredentialSupport.Ids(_conn, JWTToken.AccessToken).Should().NotBeEmpty();

        (await Run()).Should().BeOfType<SecretMigrationOutcome.Moved>();

        LegacyCredentialSupport.Ids(_conn, JWTToken.AccessToken).Should().BeEmpty();
        (await AuthTestSupport.ReadToken(_store)).Should().BeNull();
    }

    [Fact]
    public void HasAStableName() => new NexusOAuthTokenMigration(_conn, NullLogger<NexusOAuthTokenMigration>.Instance).Name.Should().Be("nexus-oauth-token");
}
