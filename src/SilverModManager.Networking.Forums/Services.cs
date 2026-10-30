using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums;

[PublicAPI]
public static class Services
{
    /// <summary>
    /// Registers the forum sign-in pieces: the secret keys the tokens are stored under and the client id
    /// override settings.
    /// </summary>
    public static IServiceCollection AddForums(this IServiceCollection collection)
    {
        return collection
            .AddSecretKeys(ForumSecretKeys.All)
            .AddSettings<ForumAuthSettings>();
    }
}
