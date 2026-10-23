using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;
using NSubstitute;
using NexusMods.Abstractions.Steam;
using NexusMods.Networking.Steam.DTOs;
using Xunit;

namespace NexusMods.Networking.Steam.Tests;

public class SecretStoreAuthStorageTests
{
    private readonly InMemorySecretStore _store = new();
    private readonly SecretStoreAuthStorage _storage;

    public SecretStoreAuthStorageTests() => _storage = new SecretStoreAuthStorage(_store, NullLogger<SecretStoreAuthStorage>.Instance);

    [Fact]
    public async Task SavedAuthDataLoadsBack()
    {
        await _storage.SaveAsync(new AuthData { Username = "user", RefreshToken = "refresh" }.Save(), CancellationToken.None);

        var loaded = (await _storage.TryLoadAsync(CancellationToken.None)).Should().BeOfType<AuthLoadResult.Loaded>().Subject;

        var data = AuthData.Load(loaded.Data);
        data.Username.Should().Be("user");
        data.RefreshToken.Should().Be("refresh");
    }

    [Fact]
    public async Task MissingSecretIsNone()
    {
        (await _storage.TryLoadAsync(CancellationToken.None)).Should().BeOfType<AuthLoadResult.None>();
    }

    [Fact]
    public async Task UnavailableStoreIsUnavailableWithItsReason()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var result = (await _storage.TryLoadAsync(CancellationToken.None)).Should().BeOfType<AuthLoadResult.Unavailable>().Subject;

        result.Reason.Should().Contain("the wallet is locked");
    }

    [Fact]
    public async Task SavingToAnUnavailableStoreThrowsWithTheReason()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var act = () => _storage.SaveAsync([1, 2, 3], CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("the wallet is locked");
    }

    [Fact]
    public async Task SavingToAReadOnlyStoreThrowsWithTheSource()
    {
        var store = Substitute.For<ISecretStore>();
        store.SetAsync(SteamSecretKeys.Auth, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<SecretWriteResult>(new SecretWriteResult.ReadOnly("SILVERMM_SECRET_STEAM_AUTH")));
        var storage = new SecretStoreAuthStorage(store, NullLogger<SecretStoreAuthStorage>.Instance);

        var act = () => storage.SaveAsync([1], CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("SILVERMM_SECRET_STEAM_AUTH");
    }

    [Fact]
    public async Task DeleteRemovesTheKey()
    {
        await _storage.SaveAsync([1, 2, 3], CancellationToken.None);

        await _storage.DeleteAsync(CancellationToken.None);

        (await _store.GetAsync(SteamSecretKeys.Auth, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        (await _storage.TryLoadAsync(CancellationToken.None)).Should().BeOfType<AuthLoadResult.None>();
    }

    [Fact]
    public async Task DeletingFromAnUnavailableStoreThrowsWithTheReason()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "the wallet is locked");

        var act = () => _storage.DeleteAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("the wallet is locked");
    }
}
