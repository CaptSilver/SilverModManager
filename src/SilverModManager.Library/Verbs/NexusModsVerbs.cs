using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Verbs;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// Verbs that read the Nexus Mods catalog of the loadout's game. The <c>nexus</c> module itself is declared next to
/// <c>nexus verify</c>; a second declaration would throw, so this only adds verbs to it.
/// </summary>
internal static class NexusModsVerbs
{
    internal static IServiceCollection AddNexusModsVerbs(this IServiceCollection services) => services
        .AddOptionParser<NexusModsSort>(text => Enum.TryParse<NexusModsSort>(text, ignoreCase: true, out var sort)
            ? (sort, null)
            : (default, $"Invalid sort `{text}`; use one of {string.Join(", ", Enum.GetNames<NexusModsSort>().Select(name => name.ToLowerInvariant()))}"))
        .AddVerb(() => Search)
        .AddVerb(() => Files);

    // The page position is the last line of the output, on one line, so a script can split it off the rows.
    private static readonly JsonSerializerOptions OneLine = new(CliJson.Options) { WriteIndented = false };

    private sealed record SearchRow(uint ModId, string Name, string Author, string Version, int Endorsements, int Downloads,
        DateTimeOffset UpdatedAt, bool AdultContent, string Url);

    private sealed record SearchTail(int Offset, int TotalCount, bool HasMore);

    private sealed record FileRow(uint FileId, string Name, string Version, string Category, long SizeBytes, DateTimeOffset Date);

    [Verb("nexus search", "Lists the Nexus Mods catalog of the loadout's game, one page at a time")]
    private static async Task<int> Search(
        [Injected] IRenderer renderer,
        [Injected] INexusModsInstaller installer,
        [Injected] IEnumerable<IGameData> games,
        [Injected] INexusModsBrowser browser,
        [Injected] IAdultContentSetting adultContent,
        [Injected] IGameDomainToGameIdMappingCache domains,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game's Nexus catalog to search")] Loadout.ReadOnly loadout,
        [Option("q", "query", "Text the mod name contains; leave it out to list everything", true)] string? query,
        [Option("s", "sort", "endorsements (the default), downloads, updated or name", true)] NexusModsSort? sort,
        [Option("p", "page", "Page number, starting at 1", true)] int? page,
        [Option("a", "adult", "Include adult mods even when the app's Show adult content setting is off", true)] bool adult,
        [Option("j", "json", "Print the rows as JSON, then the page position", true)] bool json)
    {
        var pageNumber = page ?? 1;
        if (pageNumber < 1) throw new LoadoutVerbRefusedException("The page number starts at 1.");

        var game = GameOrRefuse(installer, games, loadout);
        var searched = new NexusModsSearchQuery(game, (query ?? "").Trim(), sort ?? NexusModsSort.Endorsements,
            (pageNumber - 1) * NexusModsSearchQuery.DefaultPageSize, NexusModsSearchQuery.DefaultPageSize,
            adult || adultContent.ShowAdultContent);

        NexusModsSearchPage result;
        try
        {
            result = await browser.Search(searched, token);
        }
        catch (Exception e) when (e is InvalidOperationException || IsUnreachable(e, token))
        {
            throw new LoadoutVerbRefusedException($"Nexus Mods did not answer: {e.Message}");
        }

        var domain = domains.GetDomain(game);
        if (json)
        {
            var rows = result.Hits.Select(hit => new SearchRow(hit.ModId.Value, hit.Name, hit.Author, hit.Version, hit.Endorsements,
                hit.Downloads, hit.UpdatedAt, hit.AdultContent, NexusModsUrlBuilder.GetModUri(domain, hit.ModId).ToString()));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            await renderer.TextLine(JsonSerializer.Serialize(new SearchTail(result.Offset, result.TotalCount, result.HasMore), OneLine));
            return 0;
        }

        if (result.Hits.Count == 0)
        {
            await renderer.TextLine(result.TotalCount > 0
                ? $"Page {pageNumber} is past the last match; there are {result.TotalCount} in all."
                : searched.SearchText.Length == 0
                    ? "Nexus Mods lists no mods for this game."
                    : $"No mod matches '{searched.SearchText}'.");
            return 0;
        }

        await renderer.Table(["Mod id", "Name", "Author", "Version", "Endorsements", "Downloads", "Updated"], result.Hits.Select(hit => new object[]
        {
            hit.ModId.Value,
            hit.Name,
            hit.Author,
            hit.Version,
            hit.Endorsements,
            hit.Downloads,
            hit.UpdatedAt.ToString("yyyy-MM-dd"),
        }));
        var first = result.Offset + 1;
        var last = result.Offset + result.Hits.Count;
        await renderer.TextLine($"Showing {first}-{last} of {result.TotalCount}." + (result.HasMore ? $" Page {pageNumber + 1} has more." : ""));
        return 0;
    }

    [Verb("nexus files", "Lists the files of a Nexus mod with their category, so one can be picked by file id")]
    private static async Task<int> Files(
        [Injected] IRenderer renderer,
        [Injected] INexusModsInstaller installer,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game the mod belongs to")] Loadout.ReadOnly loadout,
        [Option("m", "modId", "The mod's id on Nexus Mods")] ModId modId,
        [Option("j", "json", "Print the files as JSON", true)] bool json)
    {
        var game = GameOrRefuse(installer, games, loadout);

        IReadOnlyList<NexusModsFileRow> files;
        try
        {
            files = await installer.Files(game, modId, token);
        }
        catch (InvalidOperationException e)
        {
            // The installer's own message already says Nexus did not answer, and for which mod.
            throw new LoadoutVerbRefusedException(e.Message);
        }
        catch (Exception e) when (IsUnreachable(e, token))
        {
            throw new LoadoutVerbRefusedException($"Nexus Mods did not answer for the files of mod {modId}: {e.Message}");
        }

        if (json)
        {
            var rows = files.Select(file => new FileRow(file.Id.Value, file.Name, file.Version, NexusModsFileCategoryNames.Label(file.Category), file.SizeBytes, file.Date));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
            return 0;
        }

        if (files.Count == 0)
        {
            await renderer.TextLine($"Nexus Mods has no files for mod {modId}.");
            return 0;
        }

        await renderer.Table(["File id", "Name", "Version", "Category", "Size", "Date"], files.Select(file => new object[]
        {
            file.Id.Value,
            file.Name,
            file.Version,
            NexusModsFileCategoryNames.Label(file.Category),
            Size.From((ulong)Math.Max(0, file.SizeBytes)).ToString(),
            file.Date.ToString("yyyy-MM-dd"),
        }));
        return 0;
    }

    // HttpClient reports its own timeout as a cancellation; only a cancelled command is a real one.
    private static bool IsUnreachable(Exception e, CancellationToken token) =>
        e is HttpRequestException || (e is OperationCanceledException && !token.IsCancellationRequested);

    private static NexusModsGameId GameOrRefuse(INexusModsInstaller installer, IEnumerable<IGameData> games, Loadout.ReadOnly loadout)
    {
        if (installer.GameOf(loadout.LoadoutId, out _) is { } game) return game;

        var name = GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)?.DisplayName
            : null;
        throw new LoadoutVerbRefusedException($"{name ?? "The loadout's game"} is not on Nexus Mods");
    }
}

/// <summary>The category names Nexus itself uses, which a script reads back from the site's own API.</summary>
internal static class NexusModsFileCategoryNames
{
    public static string Label(NexusModsFileCategory category) => category switch
    {
        NexusModsFileCategory.Main => "MAIN",
        NexusModsFileCategory.Update => "UPDATE",
        NexusModsFileCategory.Optional => "OPTIONAL",
        NexusModsFileCategory.OldVersion => "OLD_VERSION",
        NexusModsFileCategory.Miscellaneous => "MISCELLANEOUS",
        NexusModsFileCategory.Archived => "ARCHIVED",
        NexusModsFileCategory.Removed => "REMOVED",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No such Nexus file category"),
    };
}
