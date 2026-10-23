using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Networking.NexusWebApi;

internal static class LoginVerbs
{
    internal static IServiceCollection AddLoginVerbs(this IServiceCollection collection) =>
        collection.AddVerb(() => NexusLogin)
            .AddVerb(() => NexusLogout)
            .AddVerb(() => SetApiKey);

    [Verb("nexus-login", "Logs into the Nexus Mods API")]
    private static async Task<int> NexusLogin([Injected] LoginManager loginManager,
        [Injected] CancellationToken token)
    {
        await loginManager.LoginAsync(token);
        return 0;
    }

    [Verb("nexus-logout", "Logs out of the Nexus Mods API")]
    private static async Task<int> NexusLogout([Injected] LoginManager loginManager)
    {
        await loginManager.Logout();
        return 0;
    }

    [Verb("nexus-api-key", "Stores the key used in Nexus API calls; reads the key from stdin")]
    private static async Task<int> SetApiKey([Injected] ApiKeyMessageFactory apiKeyMessageFactory,
        [Injected] IRenderer renderer,
        [Injected] CancellationToken token)
    {
        // Never an option: a value on the command line ends up in shell history, `ps` and the log.
        return await StoreApiKeyAsync(apiKeyMessageFactory, renderer, Console.In, token);
    }

    /// <summary>
    /// Reads one line from <paramref name="input"/> and stores it as the API key.
    /// </summary>
    /// <returns>0 once stored; 1 when there is no key, the key is supplied from outside, or the store refused it.</returns>
    internal static async Task<int> StoreApiKeyAsync(ApiKeyMessageFactory apiKeyMessageFactory, IRenderer renderer, TextReader input, CancellationToken token)
    {
        var key = (await input.ReadLineAsync(token))?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            // With the app open the verb runs in the app's process, which never sees this terminal's stdin.
            await renderer.TextLine("No API key on stdin. Pipe it in, e.g. `silvermod nexus-api-key < key.txt`, with the app closed.");
            return 1;
        }

        switch (await apiKeyMessageFactory.SetApiKey(key))
        {
            case SecretWriteResult.ReadOnly readOnly:
                await renderer.TextLine("{0}", LoginManager.PinnedKeyMessage(readOnly.Source));
                return 1;
            case SecretWriteResult.Unavailable unavailable:
                await renderer.TextLine("The API key was not stored: {0}", unavailable.Reason);
                return 1;
            default:
                return 0;
        }
    }
}
