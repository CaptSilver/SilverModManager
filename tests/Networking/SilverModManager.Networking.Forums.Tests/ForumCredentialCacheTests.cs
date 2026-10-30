using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Secrets.Overlay;
using R3;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumCredentialCacheTests : IDisposable
{
    private static readonly SecretKey Key = ForumSecretKeys.LoversLab;
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemorySecretStore _store = new();
    private readonly List<IDisposable> _disposables = [];
    private ForumCredentialCache? _cacheInstance;

    // Created on first use, so a test fills the store before the cache exists: a cache that is already
    // listening would also react to each write while the test is still setting up.
    private ForumCredentialCache _cache => _cacheInstance ??= Create(_store);

    public void Dispose()
    {
        _cacheInstance?.Dispose();
        foreach (var disposable in _disposables) disposable.Dispose();
    }

    private ForumCredentialCache Create(ISecretStore store, ForumSite site = ForumSite.LoversLab)
    {
        var cache = new ForumCredentialCache(site, store, NullLogger.Instance);
        if (!ReferenceEquals(cache, _cacheInstance)) _disposables.Add(cache);
        return cache;
    }

    private static OAuthToken Token(string access = "stored-access", string? refresh = "stored-refresh")
        => new(access, refresh, Now + TimeSpan.FromHours(1));

    private static async Task Store(ISecretStore store, OAuthToken token, SecretKey? key = null)
    {
        var result = await store.SetAsync(key ?? Key, token.ToJson(), CancellationToken.None);
        if (result is not SecretWriteResult.Written) throw new InvalidOperationException($"Could not store the test token: {result}");
    }

    private static async Task<OAuthToken?> Stored(ISecretStore store, SecretKey? key = null)
        => await store.GetAsync(key ?? Key, CancellationToken.None) is SecretResult.Found found && OAuthToken.TryParse(found.Value, out var token) ? token : null;

    private OverlaySecretStore Overlay(params (string Name, string Value)[] variables)
        => new(_store, new FakeEnvironment(variables), null, new Dictionary<SecretKey, string>(), ForumSecretKeys.All);

    [Fact]
    public async Task ReadsTheStoredTokenOnFirstAccess()
    {
        await Store(_store, Token("the-token"));

        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("the-token");
        _cache.Current!.AccessToken.Should().Be("the-token");
        _cache.TokenSource.Should().Be(ForumCredentialSource.Stored);
        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task ReportsNothingForAnEmptyStore()
    {
        (await _cache.GetTokenAsync(CancellationToken.None)).Should().BeNull();

        _cache.Current.Should().BeNull();
        _cache.TokenSource.Should().Be(ForumCredentialSource.None);
        _cache.Source.Should().BeEmpty();
        _cache.HasCredential.CurrentValue.Should().BeFalse();
    }

    [Fact]
    public async Task CurrentIsNullUntilTheLoadHasRun()
    {
        await Store(_store, Token());

        _cache.Current.Should().BeNull();
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        _cache.Current.Should().NotBeNull();
    }

    [Fact]
    public async Task ASiteOnlyReadsItsOwnKey()
    {
        await Store(_store, Token("ll-token"), ForumSecretKeys.LoversLab);
        await Store(_store, Token("vp-token"), ForumSecretKeys.VectorPlexus);
        var vectorPlexus = Create(_store, ForumSite.VectorPlexus);

        (await vectorPlexus.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("vp-token");
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("ll-token");
    }

    [Fact]
    public async Task LoadsOnceHoweverManyAccessorsRace()
    {
        await Store(_store, Token());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await _cache.GetTokenAsync(CancellationToken.None);
            await _cache.EnsureLoadedAsync(CancellationToken.None);
        }));

        _store.Calls.Count(c => c == $"Get {Key}").Should().Be(1);
    }

    [Fact]
    public async Task DescribesTheStoreBeforeReadingSoWritableHasAValue()
    {
        _store.Status = _store.Status with { Writable = false };

        await _cache.EnsureLoadedAsync(CancellationToken.None);

        _cache.Writable.Should().BeFalse();
        _store.Calls.Should().StartWith(["Describe"]);
    }

    [Fact]
    public async Task PrimingIsTheSameLoad()
    {
        await Store(_store, Token());
        using var cache = Create(_store);

        await ((ISecretPrimable)cache).PrimeAsync(CancellationToken.None);

        cache.Current.Should().NotBeNull();
        _store.Calls.Count(c => c == $"Get {Key}").Should().Be(1);
    }

    [Fact]
    public async Task SettingATokenWritesItsJsonThroughTheStore()
    {
        var result = await _cache.SetTokenAsync(Token("fresh"), CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Written>();
        (await Stored(_store))!.AccessToken.Should().Be("fresh");
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");
        _cache.TokenSource.Should().Be(ForumCredentialSource.Stored);
    }

    [Fact]
    public async Task ATokenWithoutARefreshTokenIsStoredAndReadBack()
    {
        await _cache.SetTokenAsync(Token("fresh", refresh: null), CancellationToken.None);

        using var second = Create(_store);
        var token = await second.GetTokenAsync(CancellationToken.None);

        token!.AccessToken.Should().Be("fresh");
        token.CanRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task ClearingDeletesTheKeyAndOnlyThatKey()
    {
        await Store(_store, Token());
        await Store(_store, Token("other-site"), ForumSecretKeys.VectorPlexus);
        await _cache.EnsureLoadedAsync(CancellationToken.None);

        var result = await _cache.ClearAsync(CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Written>();
        (await Stored(_store)).Should().BeNull();
        (await Stored(_store, ForumSecretKeys.VectorPlexus)).Should().NotBeNull();
        _cache.Current.Should().BeNull();
        _cache.HasCredential.CurrentValue.Should().BeFalse();
    }

    [Fact]
    public async Task ASetThatTheStoreCannotTakeKeepsTheTokenInMemory()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");

        var result = await _cache.SetTokenAsync(Token("fresh"), CancellationToken.None);

        result.Should().BeEquivalentTo(new SecretWriteResult.Unavailable(SecretUnavailableKind.Locked, "wallet is locked"));
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");
        _cache.TokenSource.Should().Be(ForumCredentialSource.Memory);
        _cache.Source.Should().Be("memory (not stored)");
        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task ATokenInMemorySurvivesAReloadThatFindsTheStoreEmptyOrDown()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        await _cache.SetTokenAsync(Token("fresh"), CancellationToken.None);

        _store.Availability = null;
        (await _cache.ReloadTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");

        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");
        (await _cache.ReloadTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("fresh");
    }

    [Fact]
    public async Task ClearingWhileTheStoreIsDownStillDropsTheTokenForTheSession()
    {
        await Store(_store, Token());
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");

        var result = await _cache.ClearAsync(CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Unavailable>();
        _cache.Current.Should().BeNull();
    }

    [Fact]
    public async Task UnavailableAtLoadLeavesNothingAndASetAfterwardsIsKept()
    {
        _store.Availability = (SecretUnavailableKind.NoBus, "no session bus");
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        _cache.HasCredential.CurrentValue.Should().BeFalse();

        await _cache.SetTokenAsync(Token(), CancellationToken.None);

        _cache.HasCredential.CurrentValue.Should().BeTrue();
    }

    [Fact]
    public async Task AWriteFromElsewhereUpdatesTheCache()
    {
        await _cache.EnsureLoadedAsync(CancellationToken.None);
        var signedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _cache.HasCredential.Subscribe(value => { if (value) signedIn.TrySetResult(); });

        await Store(_store, Token("elsewhere"));

        await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await _cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("elsewhere");
    }

    [Fact]
    public async Task AWriteToTheOtherSitesKeyIsIgnored()
    {
        await _cache.EnsureLoadedAsync(CancellationToken.None);

        await Store(_store, Token("other-site"), ForumSecretKeys.VectorPlexus);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        _cache.Current.Should().BeNull();
        _store.Calls.Count(c => c == $"Get {Key}").Should().Be(1);
    }

    [Fact]
    public async Task ReloadingPicksUpAChangeTheStoreDidNotAnnounce()
    {
        var silent = new SilentSecretStore(_store);
        using var cache = Create(silent);
        await cache.EnsureLoadedAsync(CancellationToken.None);
        cache.Current.Should().BeNull();

        await Store(_store, Token("refreshed-by-another-process"));

        (await cache.ReloadTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("refreshed-by-another-process");
    }

    [Fact]
    public async Task UnlockingTheStoreReloadsTheToken()
    {
        await Store(_store, Token());
        _store.Availability = (SecretUnavailableKind.Locked, "wallet is locked");
        using var cache = Create(_store);
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
    }

    [Fact]
    public async Task ATokenFromTheEnvironmentIsAnOverrideAndRefusesAWriteAndAClear()
    {
        var pinned = Token("pinned-access");
        using var cache = Create(Overlay(("SILVERMM_SECRET_LOVERSLAB_OAUTH", pinned.ToJson())));

        (await cache.GetTokenAsync(CancellationToken.None))!.AccessToken.Should().Be("pinned-access");
        cache.TokenSource.Should().Be(ForumCredentialSource.Override);
        cache.PinnedSource.Should().Be("environment variable SILVERMM_SECRET_LOVERSLAB_OAUTH");
        cache.Source.Should().Be("environment variable SILVERMM_SECRET_LOVERSLAB_OAUTH");

        var written = await cache.SetTokenAsync(Token("typed"), CancellationToken.None);
        var cleared = await cache.ClearAsync(CancellationToken.None);

        var refusal = new SecretWriteResult.ReadOnly("environment variable SILVERMM_SECRET_LOVERSLAB_OAUTH");
        written.Should().Be(refusal);
        cleared.Should().Be(refusal);
        cache.Current!.AccessToken.Should().Be("pinned-access");
        (await Stored(_store)).Should().BeNull();
    }

    [Theory]
    [InlineData(SecretBackendKind.SecretService, "Secret Service")]
    [InlineData(SecretBackendKind.File, "private file")]
    public async Task TheSourceNamesWhereAStoredTokenLives(SecretBackendKind backend, string expected)
    {
        _store.Status = _store.Status with { Backend = backend };
        await Store(_store, Token());

        await _cache.EnsureLoadedAsync(CancellationToken.None);

        _cache.Source.Should().Be(expected);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"accessToken":"","expiresAt":"2026-10-30T12:00:00+00:00"}""")]
    public async Task AMalformedStoredTokenIsTreatedAsNoToken(string value)
    {
        await _store.SetAsync(Key, value, CancellationToken.None);

        (await _cache.GetTokenAsync(CancellationToken.None)).Should().BeNull();
        _cache.HasCredential.CurrentValue.Should().BeFalse();
    }

    [Fact]
    public async Task TheCredentialsGiveEachSiteItsOwnCache()
    {
        await Store(_store, Token("ll-token"), ForumSecretKeys.LoversLab);
        using var credentials = new ForumCredentials(_store, NullLoggerFactory.Instance);

        await credentials.PrimeAsync(CancellationToken.None);

        credentials.For(ForumSite.LoversLab).Current!.AccessToken.Should().Be("ll-token");
        credentials.For(ForumSite.VectorPlexus).Current.Should().BeNull();
        credentials.Cache(ForumSite.LoversLab).Should().BeSameAs(credentials.For(ForumSite.LoversLab));
    }
}
