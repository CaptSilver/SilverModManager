using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Secrets.Overlay;

namespace SilverModManager.Secrets.Verbs;

internal static class SecretVerbs
{
    internal static IServiceCollection AddSecretVerbs(this IServiceCollection collection)
        => collection.AddVerb(() => SecretsStatus);

    // The store was built from the option before this verb ran, so the value is not read again. The option is
    // declared so the parser accepts it.
    [Verb("secrets-status", "Shows where credentials are kept and which keys are set, never their values")]
    private static async Task<int> SecretsStatus(
        [Option("", "secrets-file", "Read credentials from this NAME=value file instead of the secret store", true)] string? secretsFile,
        [Injected] ISecretStore store,
        [Injected] IRenderer renderer,
        [Injected] CancellationToken token)
        => await ReportAsync(store, renderer, token);

    /// <summary>
    /// Prints the store's state, the names of the keys in it and the keys an override supplies. Describing a store
    /// never prompts, so a locked keyring shows as locked.
    /// </summary>
    internal static async Task<int> ReportAsync(ISecretStore store, IRenderer renderer, CancellationToken token)
    {
        var status = await store.DescribeAsync(token);
        var keys = await store.ListAsync(token);
        var overrides = store is OverlaySecretStore overlay
            ? overlay.Overrides()
            : status.OverriddenKeys.Select(key => new SecretOverride(key, "override")).ToArray();

        await renderer.TextLine("Backend: {0}", status.Backend.ToString());
        await renderer.TextLine("Detail: {0}", status.Detail);
        await renderer.TextLine("Locked: {0}", YesNo(status.Locked));
        await renderer.TextLine("Writable: {0}", YesNo(status.Writable));
        await renderer.TextLine("Unencrypted: {0}", YesNo(status.Unencrypted));
        await renderer.TextLine("Keys: {0}", keys.Count == 0 ? "none" : string.Join(", ", keys.Select(key => key.Name)));
        await renderer.TextLine("Overridden: {0}", overrides.Count == 0 ? "none" : string.Join(", ", overrides.Select(o => $"{o.Key.Name} ({o.Source})")));
        return 0;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
