using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Networking.NexusWebApi.Auth;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Networking.NexusWebApi;

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
        [Injected] INexusApiClient nexusApiClient,
        [Injected] IRenderer renderer,
        [Injected] CancellationToken token)
    {
        // Never an option: a value on the command line ends up in shell history, `ps` and the log.
        return await StoreApiKeyAsync(apiKeyMessageFactory, nexusApiClient, renderer, Console.In, token);
    }

    /// <summary>
    /// Reads one line from <paramref name="input"/>, checks it with Nexus Mods and stores it as the API key.
    /// </summary>
    /// <returns>0 once stored; 1 when there is no key, the key is supplied from outside, Nexus Mods could not be asked, or the store refused it.</returns>
    /// <exception cref="NexusApiKeyRejectedException">Nexus Mods rejected the key; nothing was stored.</exception>
    internal static async Task<int> StoreApiKeyAsync(ApiKeyMessageFactory apiKeyMessageFactory, INexusApiClient nexusApiClient, IRenderer renderer, TextReader input, CancellationToken token)
    {
        var key = (await input.ReadLineAsync(token))?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            // With the app open the verb runs in the app's process, which never sees this terminal's stdin.
            await renderer.TextLine("No API key on stdin. Pipe it in, e.g. `silvermod nexus-api-key < key.txt`, with the app closed.");
            return 1;
        }

        // A key supplied from outside wins over any stored one, so a typed key would silently do nothing.
        if (await apiKeyMessageFactory.GetPinnedSource(token) is { } pinned)
        {
            await renderer.TextLine("{0}", LoginManager.PinnedKeyMessage(pinned));
            return 1;
        }

        // Stored only once Nexus accepts it: a mistyped key would otherwise sign every later call in with a credential that fails.
        try
        {
            await nexusApiClient.Validate(key, token);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new NexusApiKeyRejectedException();
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // The key may be fine and the network not, so it is neither stored nor called wrong.
            await renderer.TextLine("The API key was not stored: could not check it with Nexus Mods: {0}", e.Message);
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
