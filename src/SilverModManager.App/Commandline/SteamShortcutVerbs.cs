using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.App.Commandline;

/// <summary>
/// Adds the app to Steam's library, removes it, and says where it stands. A refusal is a
/// <see cref="SteamShortcutRefusedException"/>, which the command line prints as plain text.
/// </summary>
public static class SteamShortcutVerbs
{
    public static IServiceCollection AddSteamShortcutVerbs(this IServiceCollection services) =>
        services
            .AddModule("steam-shortcut", "Commands for the app's entry in Steam's library")
            .AddVerb(() => Status)
            .AddVerb(() => Add)
            .AddVerb(() => Remove);

    [Verb("steam-shortcut status", "Shows whether the app is in Steam's library, and under which account")]
    private static async Task<int> Status([Injected] IRenderer renderer,
        [Injected] ISteamShortcutService service,
        [Option("a", "account", "The Steam account id, when more than one account could own the shortcut", true)] string? account)
    {
        var status = service.Status(ToAccountId(account));

        if (status.Account is { } chosen) await renderer.TextLine($"Account: {chosen.PersonaName} ({chosen.AccountId})");
        if (status.AppId is { } appId) await renderer.TextLine($"AppId: {appId}");
        await renderer.TextLine($"State: {status.State}");

        if (status.State == SteamShortcutState.AccountUndecided)
        {
            await renderer.TextLine($"More than one account could own it: {string.Join(", ", status.Candidates.Select(c => $"{c.PersonaName} ({c.AccountId})"))}. Pass one with --account.");
        }
        return 0;
    }

    [Verb("steam-shortcut add", "Adds the app to Steam's library. With Steam closed it writes the entry and its art; with Steam running, --route steam-url asks Steam to add it")]
    private static async Task<int> Add([Injected] IRenderer renderer,
        [Injected] ISteamShortcutService service,
        [Injected] CancellationToken token,
        [Option("r", "route", "file (Steam closed, the default) or steam-url (Steam running)", true)] string? route,
        [Option("a", "account", "The Steam account id, when more than one account could own the shortcut", true)] string? account)
    {
        await service.AddAsync(route ?? "file", ToAccountId(account), token);
        // Steam is already open on the URL route, and it may not have stored the entry yet.
        await renderer.TextLine(route == "steam-url"
            ? "Asked Steam to add it. It shows under the launcher script's name until it is added again with Steam closed. Run steam-shortcut status to see whether Steam stored it."
            : "Added. Start Steam to see it.");
        return 0;
    }

    [Verb("steam-shortcut remove", "Removes the app and the art it wrote from Steam's library (Steam must be closed)")]
    private static async Task<int> Remove([Injected] IRenderer renderer,
        [Injected] ISteamShortcutService service,
        [Injected] CancellationToken token,
        [Option("a", "account", "The Steam account id, when more than one account could own the shortcut", true)] string? account)
    {
        await service.RemoveAsync(ToAccountId(account), token);
        await renderer.TextLine("Removed.");
        return 0;
    }

    // Account ids go up to 2^32, past what the int parser the command line has can hold.
    private static uint? ToAccountId(string? account)
        => account is null ? null
            : uint.TryParse(account, out var id) ? id
            : throw new SteamShortcutRefusedException($"\"{account}\" is not a Steam account id. It is the number of the folder under Steam's userdata folder.", removing: false);
}
