using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums;

[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers the forum sign-in pieces: the secret keys the tokens are stored under, the client id override
    /// settings, one token cache per site, the OAuth client and the login state.
    /// </summary>
    public static IServiceCollection AddForums(this IServiceCollection collection)
    {
        collection.TryAddSingleton(TimeProvider.System);
        return collection
            .AddSecretKeys(ForumSecretKeys.All)
            .AddSettings<ForumAuthSettings>()
            .AddSingleton<ForumCredentials>()
            .AddSingleton<IForumCredentials>(sp => sp.GetRequiredService<ForumCredentials>())
            .AddSingleton<ISecretPrimable>(sp => sp.GetRequiredService<ForumCredentials>())
            .AddSingleton(sp => new ForumOAuth(
                // Not from IHttpClientFactory: its default logging handler writes request URLs, and the token
                // endpoint's requests carry the code and refresh token in the body.
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) }),
                sp.GetRequiredService<IForumCredentials>(),
                sp.GetRequiredService<ISettingsManager>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ForumOAuth>>()))
            .AddSingleton<ForumLoginManager>()
            .AddSingleton<IForumLoginState>(sp => sp.GetRequiredService<ForumLoginManager>());
    }
}
