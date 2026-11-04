using System.Text.Json;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Library.Listing;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// <c>library list</c>: what the library holds for a game or a loadout, read-only.
/// </summary>
internal static class LibraryListVerbs
{
    [Verb("library list", "Lists the library items of a game and/or a loadout with their kind, Nexus Mods ids, version, size, whether the file store holds the bytes and which loadouts link them; a dangling item (no SHA-256 and no bytes) is flagged")]
    internal static async Task<int> List(
        [Injected] IRenderer renderer,
        [Injected] LibraryListing listing,
        [Option("g", "game", "Only the items for this game, as `games list --json` prints its id (including items no game is assigned to)", true)] string? game,
        [Option("l", "loadout", "Only the items this loadout links", true)] Loadout.ReadOnly? loadout,
        [Option("j", "json", "Print the items as JSON", true)] bool json)
    {
        var rows = await listing.List(game is null ? null : GameId.From(game), loadout);
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        foreach (var row in rows)
        {
            await renderer.TextLine(Headline(row));
            if (row.NexusModId is { } mod)
                await renderer.TextLine($"    nexus game {row.NexusGameId}, mod {mod}, file {row.NexusFileId}; mod page {row.ModPageId} \"{row.ModPageName}\"");
            if (row.Host is { } host) await renderer.TextLine($"    host {host}");
            if (row.ThumbnailUri is { } thumbnail) await renderer.TextLine($"    thumbnail {thumbnail}");
            if (row.Loadouts.Length > 0) await renderer.TextLine($"    loadouts: {string.Join(", ", row.Loadouts)}");
        }

        var dangling = rows.Count(row => row.Dangling);
        await renderer.TextLine(dangling == 0 ? $"{rows.Count} items." : $"{rows.Count} items, {dangling} dangling (no SHA-256 and no bytes in the file store).");
        return 0;
    }

    private static string Headline(LibraryItemRow row)
    {
        var parts = new List<string> { $"{row.Name} [{row.Kind}] id {row.Id}" };
        if (row.Version is { } version) parts.Add($"version {version}");
        if (row.Size is { } size) parts.Add($"{size} bytes");
        if (row.InStore is { } inStore) parts.Add(inStore ? "in store" : "not in store");
        if (row.Dangling) parts.Add("dangling");
        return string.Join(", ", parts);
    }
}
