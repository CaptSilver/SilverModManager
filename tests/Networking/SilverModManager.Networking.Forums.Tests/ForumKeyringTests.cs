using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk;
using SilverModManager.Secrets.SecretService;
using SilverModManager.Secrets.SecretService.TestHelpers;

namespace SilverModManager.Networking.Forums.Tests;

/// <summary>
/// The forum token against a real gnome-keyring on a private bus with a throwaway HOME. Excluded from the default
/// run because it needs gnome-keyring-daemon and dbus-run-session; scripts/test.sh filters the trait out.
/// </summary>
[Trait("RequiresSecretService", "True")]
public class ForumKeyringTests(PrivateSecretServiceFixture fixture) : IClassFixture<PrivateSecretServiceFixture>
{
    private ServiceProvider StartStore()
    {
        Assert.SkipWhen(fixture.Service.SkipReason is not null, fixture.Service.SkipReason ?? "");
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive })
            // The private bus, and never the one of whoever runs the tests.
            .AddSingleton<IEnvironmentReader>(new FakeEnvironment(("DBUS_SESSION_BUS_ADDRESS", fixture.Service.Address)))
            .AddSecretServiceBackend()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task ATokenWrittenToTheKeyringIsReadBackByAFreshCacheAndDeletedByClear()
    {
        using var provider = StartStore();
        var store = provider.GetRequiredService<ISecretServiceStore>();
        var token = new OAuthToken("access-value", "refresh-value", new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero));
        var cancel = TestContext.Current.CancellationToken;

        (await store.SetAsync(ForumSecretKeys.LoversLab, token.ToJson(), cancel)).Should().BeOfType<SecretWriteResult.Written>();

        using var reader = new ForumCredentialCache(ForumSite.LoversLab, store, NullLogger.Instance);
        (await reader.GetTokenAsync(cancel)).Should().Be(token);
        reader.TokenSource.Should().Be(ForumCredentialSource.Stored);

        (await reader.ClearAsync(cancel)).Should().BeOfType<SecretWriteResult.Written>();

        (await store.GetAsync(ForumSecretKeys.LoversLab, cancel)).Should().BeOfType<SecretResult.NotFound>();
        using var after = new ForumCredentialCache(ForumSite.LoversLab, store, NullLogger.Instance);
        (await after.GetTokenAsync(cancel)).Should().BeNull();
    }
}
