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
        .AddVerb(() => Files)
        .AddVerb(() => Install);

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

    private sealed record InstallRow(string Name, string Outcome, bool Downloaded, bool Dependency, uint ModId, uint FileId);

    private const int ExitNeedsBrowser = 2;

    [Verb("nexus install", "Installs one Nexus mod file into the loadout, from the library or through the premium download")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] INexusModsInstaller installer,
        [Injected] ILoadoutInstallCore core,
        [Injected] ILoginManager login,
        [Injected] IEnumerable<IGameData> games,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into; its game says which Nexus game the mod is in")] Loadout.ReadOnly loadout,
        [Option("m", "modId", "The mod's id on Nexus Mods; with no file id, the mod's one main file", true)] ModId? modId,
        [Option("f", "fileId", "The file's id on Nexus Mods, as `nexus files` shows it", true)] FileId? fileId,
        [Option("j", "json", "Print the outcomes as JSON", true)] bool json)
    {
        var game = GameOrRefuse(installer, games, loadout);
        var (mod, file) = await ResolveFile(installer, game, modId, fileId, token);

        if (file.Category == NexusModsFileCategory.Removed) throw new LoadoutVerbRefusedException("Nexus Mods has removed this file");
        if (!json && file.Category is NexusModsFileCategory.Archived or NexusModsFileCategory.OldVersion)
            await renderer.TextLine($"[note] {file.Name} is {(file.Category == NexusModsFileCategory.Archived ? "archived" : "an old version")}");

        // A file the library holds installs for any account: a free member who clicked the site's download button has
        // put it there. Only a missing file needs an account that may use the download API.
        var resolved = installer.Resolve(game, mod, file.Id, file.Name, dependency: false, loadout.LoadoutId);
        if (resolved.InLibrary is null)
        {
            switch (await AccountOrRefuse(login, token))
            {
                case NexusAccountKind.SignedOut:
                    throw new LoadoutVerbRefusedException("Not signed in. Run `silvermodmanager nexus-login` or pipe a key to `nexus-api-key`");
                case NexusAccountKind.Free:
                    await PrintBrowserHandOff(renderer, installer.DownloadPage(game, mod, file.Id), json);
                    return ExitNeedsBrowser;
            }
        }

        // The same key the browse page uses, so a click there and this verb run one download between them.
        var outcomes = await core.Run(loadout.LoadoutId, $"nexus:{mod}", [resolved], token);
        if (json)
        {
            var rows = outcomes.Select(o => new InstallRow(o.Name, InstallOutcomeRenderer.OutcomeName(o.Outcome), o.Downloaded, o.Dependency, mod.Value, file.Id.Value));
            await renderer.TextLine(JsonSerializer.Serialize(rows, CliJson.Options));
        }
        else
        {
            await InstallOutcomeRenderer.Print(renderer, outcomes);
        }

        return 0;
    }

    /// <summary>The file to install and its mod, from whichever ids were given.</summary>
    private static async Task<(ModId Mod, NexusModsFileRow File)> ResolveFile(
        INexusModsInstaller installer, NexusModsGameId game, ModId? modId, FileId? fileId, CancellationToken token)
    {
        var mod = modId is { } given ? given
            : fileId is { } fileOnly ? await AskNexus(() => installer.ModOf(game, fileOnly, token), token)
            : throw new LoadoutVerbRefusedException("Say which mod to install with --modId, or which file with --fileId.");
        var files = await AskNexus(() => installer.Files(game, mod, token), token);

        if (fileId is { } wanted)
        {
            return files.FirstOrDefault(candidate => candidate.Id == wanted) is { } found
                ? (mod, found)
                : throw new LoadoutVerbRefusedException($"File {wanted} is not a file of mod {mod}; `silvermodmanager nexus files -m {mod}` lists its files");
        }

        if (NexusModsMainFile.Pick(files) is { } main) return (mod, files.First(candidate => candidate.Id == main));
        throw new LoadoutVerbRefusedException(
            $"Mod {mod} has {NexusModsMainFile.MainCount(files)} main files; pick one with `silvermodmanager nexus files -m {mod}` and pass it as -f");
    }

    // Only the installer's own calls are caught, so a fault in this verb still surfaces as one.
    private static async Task<T> AskNexus<T>(Func<Task<T>> call, CancellationToken token)
    {
        try
        {
            return await call();
        }
        catch (InvalidOperationException e)
        {
            // The installer's own message already says Nexus did not answer, or has no such file, and for which id.
            throw new LoadoutVerbRefusedException(e.Message);
        }
        catch (Exception e) when (IsUnreachable(e, token))
        {
            throw new LoadoutVerbRefusedException($"Nexus Mods did not answer: {e.Message}");
        }
    }

    // A failed account check is not "signed out": telling a signed-in user to sign in again hides the outage.
    private static async Task<NexusAccountKind> AccountOrRefuse(ILoginManager login, CancellationToken token)
    {
        try
        {
            return await NexusAccount.Current(login, token);
        }
        catch (Exception e) when (IsUnreachable(e, token))
        {
            throw new LoadoutVerbRefusedException($"Nexus Mods did not answer: {e.Message}");
        }
    }

    // No waiting: a verb that blocks on a browser click is a prompt, and the nxm link reaches the app, not this terminal.
    private static async Task PrintBrowserHandOff(IRenderer renderer, Uri page, bool json)
    {
        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new { needsBrowser = true, url = page.ToString() }, CliJson.Options));
            return;
        }

        await renderer.TextLine(page.ToString());
        await renderer.TextLine("A free account downloads through the site: click \"Download with manager\" on that page to send the file to SilverModManager.");
        await renderer.TextLine("The SilverModManager window, not this terminal, reports whether that download worked.");
        await renderer.TextLine("Run this same command again once the window shows the file.");
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
