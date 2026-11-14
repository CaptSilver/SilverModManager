using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Library.Workshop;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// The verbs of the Steam Workshop: list the items a game's Workshop folder holds, and open an item's page so Steam can
/// subscribe to it. Nothing here downloads Workshop content or writes Steam's folder.
/// </summary>
internal static class WorkshopVerbs
{
    internal static IServiceCollection AddWorkshopVerbs(this IServiceCollection services) => services
        .AddVerb(() => List)
        .AddVerb(() => Open)
        .AddModule("workshop", "Commands for the Steam Workshop items of a game");

    private sealed record Item(string Id, string Title, string? Updated, bool UpdatedBySteam);

    [Verb("workshop list", "Lists the Steam Workshop items the loadout's game has subscribed to, and which of them Steam updated since its folder last changed")]
    private static async Task<int> List(
        [Injected] IRenderer renderer,
        [Injected] WorkshopItemsView view,
        [Injected] IGameRegistry registry,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's Workshop items to list")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the items as JSON", true)] bool json)
    {
        if (!registry.TryGetGameInstallation(loadout, out var install))
            throw new LoadoutVerbRefusedException("The loadout's game is not installed, so there is no Workshop folder to read.");
        if (SteamWorkshopGame.For(install.Game) is null)
            throw new LoadoutVerbRefusedException($"{install.Game.DisplayName} has no Steam Workshop items.");

        var rows = await view.For(install, token);
        var items = rows.Select(row => new Item(row.Id.ToString(CultureInfo.InvariantCulture), row.Title, row.Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.UpdatedBySteam)).ToArray();
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(items, CliJson.Options));
            return 0;
        }

        if (items.Length == 0)
        {
            await renderer.TextLine("The game's Workshop folder holds no items.");
            return 0;
        }

        await renderer.Table(["Id", "Name", "Updated", "Updated by Steam"], items.Select(item => new object[] { item.Id, item.Title, item.Updated ?? "-", item.UpdatedBySteam ? "yes" : "" }));
        return 0;
    }

    [Verb("workshop open", "Opens a Steam Workshop item's page, where it can be subscribed to; Steam downloads it, this app does not")]
    private static async Task<int> Open(
        [Injected] IRenderer renderer,
        [Injected] IOSInterop os,
        [Option("i", "id", "The item's id, the number in its page address")] string id,
        [Option("s", "steam", "Open the item in the Steam client rather than the browser", true)] bool steam)
    {
        if (!ulong.TryParse(id.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            throw new LoadoutVerbRefusedException($"'{id}' is not a Workshop item id; it is the number in the item's page address");

        var link = steam ? SteamWorkshopLinks.Subscribe(itemId) : SteamWorkshopLinks.WebPage(itemId);
        os.OpenUri(link);
        await renderer.TextLine($"Opened {link}");
        return 0;
    }
}
