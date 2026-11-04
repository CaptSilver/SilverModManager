using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Secrets.Overlay;
using R3;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class NexusCredentialCacheTests : IDisposable
{
    private readonly InMemorySecretStore _store = new();
    private NexusCredentialCache? _cacheInstance;

    // Created on first use, so a test fills the store before the cache exists: a cache that is already
    // listening would also react to each write while the test is still setting up.
    private NexusCredentialCache _cache => _cacheInstance ??= AuthTestSupport.CreateCache(_store);

    public void Dispose() => _cacheInstance?.Dispose();

    private OverlaySecretStore Overlay(params (string Name, string Value)[] variables) => new(
        _store,
        new FakeEnvironment(variables),
        null,
        new Dictionary<SecretKey, string> { [NexusSecretKeys.ApiKey] = NexusSecretKeys.ApiKeyEnvironmentVariable });

    [Fact]
    public async Task ReadsTheStoredKeyAndTokenOnFirstAccess()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        await AuthTestSupport.StoreToken(_store, "the-token", TimeSpan.FromHours(1));

        (await _cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("the-key");
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("the-token");
        _cache.ApiKeySource.Should().Be(CredentialSource.Stored);
        _cache.TokenSource.Should().Be(CredentialSource.Stored);
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyStore()
    {
        (await _cache.GetApiKeyAsync(CancellationToken.None)).Should().BeNull();
        (await _cache.GetTokenAsync(CancellationToken.None)).Should().BeNull();
        _cache.ApiKeySource.Should().Be(CredentialSource.None);
        _cache.TokenSource.Should().Be(CredentialSource.None);
        _cache.HasCredential.CurrentValue.Should().BeFalse();
        _cache.Kind.Should().Be(NexusCredentialKind.None);
    }

    [Fact]
    public async Task DescribesTheStoreBeforeReadingSoWritableHasAValue()
    {
        _store.Status = _store.Status with { Writable = false };

        await _cache.EnsureLoadedAsync(CancellationToken.None);

        _cache.Writable.Should().BeFalse();
        _store.Calls.Should().StartWith(["Describe"]);
        _store.Calls.Should().Contain("Get nexus.api-key").And.Contain("Get nexus.oauth");
    }

    [Fact]
    public async Task LoadsOnceHoweverManyAccessorsRace()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await _cache.GetApiKeyAsync(CancellationToken.None);
            await _cache.GetTokenAsync(CancellationToken.None);
            await _cache.EnsureLoadedAsync(CancellationToken.None);
        }));

        _store.Calls.Count(c => c == "Get nexus.api-key").Should().Be(1);
        _store.Calls.Count(c => c == "Get nexus.oauth").Should().Be(1);
    }

    [Fact]
    public async Task PrimeAsyncIsTheSameLoad()
    {
        // Its own store and a cache built after the write: a cache that exists earlier also reloads on the
        // store's change notification.
        var store = new InMemorySecretStore();
        await AuthTestSupport.StoreKey(store, "the-key");
        using var cache = AuthTestSupport.CreateCache(store);

        await ((ISecretPrimable)cache).PrimeAsync(CancellationToken.None);
        await cache.EnsureLoadedAsync(CancellationToken.None);

        cache.HasCredential.CurrentValue.Should().BeTrue();
        cache.ApiKeySource.Should().Be(CredentialSource.Stored);
        store.Calls.Count(c => c == "Get nexus.api-key").Should().Be(1);
    }

    [Fact]
    public async Task ASubscriberThatArrivesAfterTheLoadGetsTheCurrentValue()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        await _cache.EnsureLoadedAsync(CancellationToken.None);

        var seen = new List<bool>();
        using var subscription = _cache.HasCredential.Subscribe(seen.Add);

        seen.Should().Equal(true);
    }

    [Fact]
    public async Task SettingTheKeyWritesThroughTheStore()
    {
        var result = await _cache.SetApiKeyAsync("the-key", CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Written>();
        (await AuthTestSupport.ReadKey(_store)).Should().Be("the-key");
        _cache.ApiKeySource.Should().Be(CredentialSource.Stored);
        _cache.Kind.Should().Be(NexusCredentialKind.ApiKey);
    }

    [Fact]
    public async Task SettingATokenFromAReplyStoresItsJson()
    {
        var result = await _cache.SetTokenAsync(AuthTestSupport.Reply("fresh"), CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Written>();
        (await AuthTestSupport.ReadToken(_store))!.AccessToken.Should().Be("fresh");
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");
        _cache.Kind.Should().Be(NexusCredentialKind.OAuthToken);
    }

    [Fact]
    public async Task ClearingDeletesBothKeys()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        await AuthTestSupport.StoreToken(_store, "the-token", TimeSpan.FromHours(1));
        await _cache.EnsureLoadedAsync(CancellationToken.None);

        var result = await _cache.ClearAsync(CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Written>();
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
        (await AuthTestSupport.ReadToken(_store)).Should().BeNull();
        _cache.HasCredential.CurrentValue.Should().BeFalse();
    }

    [Fact]
    public async Task ASetThatTheStoreCannotTakeKeepsTheValueInMemory()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");

        var result = await _cache.SetApiKeyAsync("the-key", CancellationToken.None);

        result.Should().BeEquivalentTo(new SecretWriteResult.Unavailable(SecretUnavailableKind.Locked, "wallet is locked"));
        (await _cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("the-key");
        _cache.ApiKeySource.Should().Be(CredentialSource.Memory);
        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task AValueInMemorySurvivesAReloadThatFindsTheStoreEmptyOrDown()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        await _cache.SetApiKeyAsync("the-key", CancellationToken.None);

        _store.Availability = null;
        _store.Status = _store.Status with { Locked = false };
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        (await _cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("the-key");
    }

    [Fact]
    public async Task UnavailableAtLoadLeavesNothingAndASetAfterwardsIsKept()
    {
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        _cache.HasCredential.CurrentValue.Should().BeFalse();

        await _cache.SetApiKeyAsync("the-key", CancellationToken.None);

        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task AWriteFromElsewhereUpdatesTheCache()
    {
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        var signedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _cache.HasCredential.Subscribe(value => { if (value) signedIn.TrySetResult(); });

        await AuthTestSupport.StoreKey(_store, "elsewhere");

        await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await _cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("elsewhere");
    }

    [Fact]
    public async Task UnlockingTheStoreReloadsBothKeys()
    {
        await AuthTestSupport.StoreKey(_store, "the-key");
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        using var cache = AuthTestSupport.CreateCache(_store);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        cache.HasCredential.CurrentValue.Should().BeFalse();
        var signedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = cache.HasCredential.Subscribe(value => { if (value) signedIn.TrySetResult(); });

        _store.Availability = null;
        _store.Status = _store.Status with { Locked = false };

        await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WritableFollowsTheStatus()
    {
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        _cache.Writable.Should().BeTrue();

        _store.Status = _store.Status with { Writable = false, Locked = true };
        _cache.Writable.Should().BeFalse();

        _store.Status = _store.Status with { Writable = true, Locked = false };
        _cache.Writable.Should().BeTrue();
    }

    [Fact]
    public async Task AKeyFromTheEnvironmentIsAnOverrideAndRefusesAWrite()
    {
        using var cache = AuthTestSupport.CreateCache(Overlay((NexusSecretKeys.ApiKeyEnvironmentVariable, "pinned")));

        (await cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("pinned");
        cache.ApiKeySource.Should().Be(CredentialSource.Override);
        cache.PinnedApiKeySource.Should().Be("environment variable NEXUS_API_KEY");
        cache.Kind.Should().Be(NexusCredentialKind.ApiKey);
        cache.Source.Should().Be("environment variable NEXUS_API_KEY");

        var result = await cache.SetApiKeyAsync("typed", CancellationToken.None);

        result.Should().Be(new SecretWriteResult.ReadOnly("environment variable NEXUS_API_KEY"));
        (await cache.GetApiKeyAsync(CancellationToken.None)).Should().Be("pinned");
        (await AuthTestSupport.ReadKey(_store)).Should().BeNull();
    }

    [Theory]
    [InlineData(SecretBackendKind.SecretService, "Secret Service")]
    [InlineData(SecretBackendKind.File, "private file")]
    public async Task TheSummaryNamesWhereAStoredKeyLives(SecretBackendKind backend, string expected)
    {
        _store.Status = _store.Status with { Backend = backend };
        await AuthTestSupport.StoreKey(_store, "the-key");

        await _cache.EnsureLoadedAsync(CancellationToken.None);

        _cache.Source.Should().Be(expected);
        _cache.Kind.Should().Be(NexusCredentialKind.ApiKey);
    }

    [Fact]
    public async Task TheSummaryPrefersTheKeyOverTheToken()
    {
        await AuthTestSupport.StoreToken(_store, "the-token", TimeSpan.FromHours(1));
        await AuthTestSupport.StoreKey(_store, "the-key");

        await _cache.EnsureLoadedAsync(CancellationToken.None);

        _cache.Kind.Should().Be(NexusCredentialKind.ApiKey);
    }

    [Fact]
    public async Task AMalformedStoredTokenIsTreatedAsNoToken()
    {
        await _store.SetAsync(NexusSecretKeys.OAuth, "not json", CancellationToken.None);

        (await _cache.GetTokenAsync(CancellationToken.None)).Should().BeNull();
        _cache.HasCredential.CurrentValue.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentReloadsLeaveTheNewestStoredToken()
    {
        var inner = new InMemorySecretStore();
        await AuthTestSupport.StoreToken(inner, "first", TimeSpan.FromHours(1));
        var store = new HoldingSecretStore(inner);
        using var cache = AuthTestSupport.CreateCache(store);
        await cache.EnsureLoadedAsync(CancellationToken.None);

        // The first reload reads "first" and is then held up on its way back to the cache.
        var release = store.HoldNextTokenRead();
        var slow = cache.ReloadTokenAsync(CancellationToken.None).AsTask();
        await store.HeldRead.WaitAsync(TimeSpan.FromSeconds(10));

        // Another process stores a newer token and a second reload picks it up while the first is still held.
        await AuthTestSupport.StoreToken(inner, "second", TimeSpan.FromHours(1));
        var fast = cache.ReloadTokenAsync(CancellationToken.None).AsTask();
        await Task.Delay(200);
        release();

        await Task.WhenAll(slow, fast).WaitAsync(TimeSpan.FromSeconds(10));

        (await cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("second");
    }

    [Fact]
    public async Task ReloadsRequestedWhileOneIsRunningShareASingleFollowUpRead()
    {
        var inner = new InMemorySecretStore();
        await AuthTestSupport.StoreToken(inner, "first", TimeSpan.FromHours(1));
        var store = new HoldingSecretStore(inner);
        using var cache = AuthTestSupport.CreateCache(store);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        var readsBefore = store.TokenReads;

        var release = store.HoldNextTokenRead();
        var running = cache.ReloadTokenAsync(CancellationToken.None).AsTask();
        await store.HeldRead.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = Enumerable.Range(0, 5).Select(_ => cache.ReloadTokenAsync(CancellationToken.None).AsTask()).ToArray();
        await Task.Delay(200);
        release();

        await Task.WhenAll(waiting.Append(running)).WaitAsync(TimeSpan.FromSeconds(10));

        store.TokenReads.Should().Be(readsBefore + 2, "the held read, then one read that serves every reload requested meanwhile");
    }

    /// <summary>
    /// Reads the token at once but can hold the answer back, the way a slow keyring reply arrives after a later one.
    /// Never reports changes, so only explicit reloads reach the cache.
    /// </summary>
    private sealed class HoldingSecretStore(InMemorySecretStore inner) : ISecretStore
    {
        private TaskCompletionSource? _gate;
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _tokenReads;

        public Task HeldRead => _held.Task;

        public int TokenReads => Volatile.Read(ref _tokenReads);

        public Action HoldNextTokenRead()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _gate, gate);
            return () => gate.TrySetResult();
        }

        public Observable<SecretKey> Changed => Observable.Never<SecretKey>();

        public Observable<SecretStoreStatus> StatusChanged => Observable.Never<SecretStoreStatus>();

        public async ValueTask<SecretResult> GetAsync(SecretKey key, CancellationToken cancellationToken)
        {
            var result = await inner.GetAsync(key, cancellationToken);
            if (key != NexusSecretKeys.OAuth) return result;

            Interlocked.Increment(ref _tokenReads);
            if (Interlocked.Exchange(ref _gate, null) is { } gate)
            {
                _held.TrySetResult();
                await gate.Task;
            }

            return result;
        }

        public ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken) => inner.SetAsync(key, value, cancellationToken);

        public ValueTask<SecretWriteResult> DeleteAsync(SecretKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);

        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

        public ValueTask<SecretStoreStatus> DescribeAsync(CancellationToken cancellationToken) => inner.DescribeAsync(cancellationToken);

        public ValueTask<SecretStoreStatus> UnlockAsync(CancellationToken cancellationToken) => inner.UnlockAsync(cancellationToken);
    }
}
