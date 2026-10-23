using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Secrets;

namespace NexusMods.Networking.GOG.Tests;

public class GogCredentialCacheTests : IDisposable
{
    private static readonly GogCredential Credential = new("access", "refresh", "session", 5UL, new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly InMemorySecretStore _store = new();
    private GogCredentialCache? _instance;

    // Created on first use so a test can fill the store before the cache starts listening.
    private GogCredentialCache Cache => _instance ??= new GogCredentialCache(_store, NullLogger<GogCredentialCache>.Instance);

    public void Dispose() => _instance?.Dispose();

    [Fact]
    public async Task ReadsTheStoredCredentialOnFirstAccess()
    {
        await _store.SetAsync(GogSecretKeys.OAuth, Credential.ToJson(), CancellationToken.None);

        (await Cache.GetAsync(CancellationToken.None)).Should().Be(Credential);
        Cache.Current.Should().Be(Credential);
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyStore() => (await Cache.GetAsync(CancellationToken.None)).Should().BeNull();

    [Fact]
    public async Task ASetWritesTheOneValue()
    {
        (await Cache.SetAsync(Credential, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        var found = (SecretResult.Found)await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None);
        GogCredential.TryParse(found.Value, out var stored).Should().BeTrue();
        stored.Should().Be(Credential);
    }

    [Fact]
    public async Task AnUnavailableStoreKeepsTheCredentialInMemory()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "locked");

        (await Cache.SetAsync(Credential, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Unavailable>();

        (await Cache.GetAsync(CancellationToken.None)).Should().Be(Credential);
    }

    [Fact]
    public async Task PicksUpAChangeMadeThroughTheStore()
    {
        await Cache.EnsureLoadedAsync(CancellationToken.None);

        await _store.SetAsync(GogSecretKeys.OAuth, Credential.ToJson(), CancellationToken.None);

        await WaitFor(() => Cache.Current == Credential);
    }

    [Fact]
    public async Task ReloadsAfterTheStoreUnlocks()
    {
        await _store.SetAsync(GogSecretKeys.OAuth, Credential.ToJson(), CancellationToken.None);
        _store.Availability = (SecretUnavailableKind.Locked, "locked");
        await Cache.EnsureLoadedAsync(CancellationToken.None);
        Cache.Current.Should().BeNull();

        _store.Availability = null;
        _store.Status = _store.Status with { Locked = false };

        await WaitFor(() => Cache.Current == Credential);
    }

    [Fact]
    public async Task ClearDeletesTheKeyAndForgetsTheCredential()
    {
        await Cache.SetAsync(Credential, CancellationToken.None);

        (await Cache.ClearAsync(CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        (await _store.GetAsync(GogSecretKeys.OAuth, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        (await Cache.GetAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PrimingLoadsTheStore()
    {
        await _store.SetAsync(GogSecretKeys.OAuth, Credential.ToJson(), CancellationToken.None);

        await ((ISecretPrimable)Cache).PrimeAsync(CancellationToken.None);

        _store.Calls.Should().Contain("Get gog.oauth");
        Cache.Current.Should().Be(Credential);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The cache did not catch up.");
            await Task.Delay(10);
        }
    }
}
