using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Secrets;
using NexusMods.Networking.NexusWebApi.Auth;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Networking.NexusWebApi;

internal static class LoginVerbs
{
    internal static IServiceCollection AddLoginVerbs(this IServiceCollection collection) =>
        collection.AddVerb(() => NexusLogin)
            .AddVerb(() => NexusLogout)
            .AddVerb(() => SetApiKey)
            .AddVerb(() => NexusWhoAmI);

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

    // The credential was chosen when the process built its secret store from the option, so the value is not read
    // again here. The option is declared so the parser accepts it.
    [Verb("nexus-whoami", "Checks the Nexus Mods credential this process holds and prints the account it belongs to")]
    private static async Task<int> NexusWhoAmI(
        [Option("", "secrets-file", "Read credentials from this NAME=value file instead of the secret store", true)] string? secretsFile,
        [Injected] ILoginManager loginManager,
        [Injected] INexusCredentialSummary credentials,
        [Injected] IRenderer renderer,
        [Injected] CancellationToken token)
        => await WhoAmIAsync(loginManager, credentials, renderer, token);

    /// <summary>
    /// Asks Nexus who the credential belongs to. This is the call that proves a headless process reaches Nexus
    /// with what it holds; a launch itself never calls Nexus.
    /// </summary>
    /// <returns>0 with the account, its role and where the credential came from; 1 when there is no usable credential or Nexus cannot be asked.</returns>
    internal static async Task<int> WhoAmIAsync(ILoginManager loginManager, INexusCredentialSummary credentials, IRenderer renderer, CancellationToken token)
    {
        UserInfo? user;
        try
        {
            user = await loginManager.GetUserInfoAsync(token);
        }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await renderer.TextLine("Not signed in");
            await renderer.TextLine("Nexus rejected the credential from {0}", credentials.Source);
            return 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Not "Not signed in": the credential may be fine and the network not.
            await renderer.TextLine("Could not verify the credential: {0}", e.Message);
            return 1;
        }

        if (user is null)
        {
            await renderer.TextLine("Not signed in");
            return 1;
        }

        await renderer.TextLine("{0} ({1}) via {2}", user.Name, user.UserRole.ToString(), credentials.Source);
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
