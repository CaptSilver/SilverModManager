using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// Search, install and update-check verbs for the games whose mods come from Thunderstore. They read the cached
/// package index of the loadout's game, refreshing it first when it is missing or old.
/// </summary>
internal static class ThunderstoreVerbs
{
    private const int MaxSearchRows = 50;

    internal static IServiceCollection AddThunderstoreVerbs(this IServiceCollection services) => services
        .AddVerb(() => Refresh)
        .AddVerb(() => Search)
        .AddVerb(() => Install)
        .AddVerb(() => Updates)
        .AddModule("thunderstore", "Commands for games whose mods come from Thunderstore");

    [Verb("thunderstore refresh", "Downloads the package index of the loadout's game, fetching only the parts that changed")]
    private static async Task<int> Refresh(
        [Injected] IRenderer renderer,
        [Injected] ThunderstoreIndexCache cache,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game says which Thunderstore community to refresh")] Loadout.ReadOnly loadout)
    {
        if (!TryGetGame(loadout, out var game, out var problem))
        {
            await renderer.TextLine(problem);
            return 1;
        }

        await cache.Refresh(game.ThunderstoreCommunity, token);
        var catalog = await cache.Load(game.ThunderstoreCommunity, token);
        await renderer.TextLine($"Refreshed the '{game.ThunderstoreCommunity}' index: {catalog.Count} packages.");
        return 0;
    }

    [Verb("thunderstore search", "Lists the packages of the loadout's game whose name or team contains the query, best rated first")]
    private static async Task<int> Search(
        [Injected] IRenderer renderer,
        [Injected] ThunderstoreIndexCache cache,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game says which Thunderstore community to search")] Loadout.ReadOnly loadout,
        [Option("q", "query", "Text the package name or team contains")] string query)
    {
        if (!TryGetGame(loadout, out var game, out var problem))
        {
            await renderer.TextLine(problem);
            return 1;
        }

        var catalog = await LoadCatalog(cache, game.ThunderstoreCommunity, token);
        var found = catalog.Search(query, includeDeprecated: true);
        if (found.Count == 0)
        {
            await renderer.TextLine($"No package in '{game.ThunderstoreCommunity}' matches '{query}'.");
            return 0;
        }

        var rows = found.Take(MaxSearchRows).Select(package => new object[]
        {
            package.FullName,
            package.Versions.FirstOrDefault(version => version.IsActive)?.VersionNumber ?? "-",
            package.RatingScore,
            package.IsDeprecated ? "yes" : "no",
        });
        await renderer.Table(["Package", "Latest", "Rating", "Deprecated"], rows);
        if (found.Count > MaxSearchRows) await renderer.TextLine($"Showing the best rated {MaxSearchRows} of {found.Count} matches; narrow the query to see others.");
        return 0;
    }

    [Verb("thunderstore install", "Downloads a package with the packages it needs and installs them into the loadout")]
    private static async Task<int> Install(
        [Injected] IRenderer renderer,
        [Injected] IServiceProvider provider,
        [Injected] IConnection connection,
        [Injected] ThunderstoreIndexCache cache,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into; its game says which Thunderstore community the package is in")] Loadout.ReadOnly loadout,
        [Option("p", "package", "Namespace-Name for the latest version, or Namespace-Name-1.2.3 for one version")] string package,
        [Option("e", "exact", "Take the dependency versions the package lists instead of the latest ones", true)] bool exact)
    {
        if (!TryGetGame(loadout, out var game, out var problem))
        {
            await renderer.TextLine(problem);
            return 1;
        }

        var catalog = await LoadCatalog(cache, game.ThunderstoreCommunity, token);
        if (!TryResolvePackage(catalog, package, out var root, out problem))
        {
            await renderer.TextLine(problem);
            return 1;
        }

        var inLibraryBefore = ThunderstoreLibraryFile.All(connection.Db).Select(file => file.Id).ToHashSet();
        var request = new ThunderstoreInstallRequest(
            root,
            game,
            WithDependencies: true,
            exact ? DependencyPolicy.Exact : DependencyPolicy.Latest,
            ExpectedSha256: null,
            ThunderstoreLoadoutPackages.Installed(connection.Db, loadout.LoadoutId));
        IReadOnlyList<LibraryFile.ReadOnly> files;
        try
        {
            files = await provider.GetRequiredService<IThunderstoreSource>().AddAsync(request, token);
        }
        catch (Exception e) when (e is DependencyResolutionException or ThunderstoreIntegrityException)
        {
            await renderer.TextLine(e.Message);
            return 1;
        }

        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        var changed = 0;
        foreach (var file in files)
        {
            var item = file.AsLibraryItem();
            var name = file.TryGetAsThunderstoreLibraryFile(out var thunderstore)
                ? $"{thunderstore.Namespace}-{thunderstore.Name}-{thunderstore.VersionNumber}"
                : file.FileName.ToString();

            if (!inLibraryBefore.Contains(file.Id)) await renderer.TextLine($"[downloaded] {name}");

            LoadoutPackageOutcome outcome;
            try
            {
                outcome = await ThunderstoreLoadoutPackages.InstallOrEnable(connection, item, loadout.LoadoutId, async () => await loadoutManager.InstallItem(item, loadout.LoadoutId));
            }
            catch (ThunderstorePackageInDisabledGroupException e)
            {
                await renderer.TextLine(e.Message);
                return 1;
            }

            switch (outcome)
            {
                case LoadoutPackageOutcome.AlreadyEnabled:
                    await renderer.TextLine($"[already in loadout] {name}");
                    break;
                case LoadoutPackageOutcome.Enabled:
                    await renderer.TextLine($"[enabled] {name}");
                    changed++;
                    break;
                case LoadoutPackageOutcome.Installed:
                    await renderer.TextLine($"[installed] {name}");
                    changed++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
            }
        }

        if (changed == 0) await renderer.TextLine("Nothing new to install.");
        return 0;
    }

    [Verb("thunderstore updates", "Lists the Thunderstore packages in the library that have a newer version in the loadout game's index")]
    private static async Task<int> Updates(
        [Injected] IRenderer renderer,
        [Injected] IConnection connection,
        [Injected] ThunderstoreIndexCache cache,
        [Injected] ThunderstoreUpdateChecker checker,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose game says which Thunderstore community to check")] Loadout.ReadOnly loadout)
    {
        if (!TryGetGame(loadout, out var game, out var problem))
        {
            await renderer.TextLine(problem);
            return 1;
        }

        var catalog = await LoadCatalog(cache, game.ThunderstoreCommunity, token);
        var updates = checker.Check(game.ThunderstoreCommunity, catalog, connection.Db);
        if (updates.Count == 0)
        {
            await renderer.TextLine("Every Thunderstore package in the library is up to date.");
            return 0;
        }

        await renderer.Table(["Package", "Installed", "Newest"], updates.Select(update => new object[]
        {
            update.Current.Package.ToString(),
            update.Current.Version.ToString(),
            update.Newest.Version.ToString(),
        }));
        return 0;
    }

    private static bool TryGetGame(Loadout.ReadOnly loadout, [NotNullWhen(true)] out IThunderstoreGame? game, out string problem)
    {
        var data = loadout.InstallationInstance.Game;
        if (ThunderstoreGame.For(data) is { } found)
        {
            game = found;
            problem = string.Empty;
            return true;
        }

        game = null;
        problem = $"{data.DisplayName} has no Thunderstore support: its mods do not come from Thunderstore.";
        return false;
    }

    private static async Task<ThunderstoreCatalog> LoadCatalog(ThunderstoreIndexCache cache, string community, CancellationToken token)
    {
        if (cache.IsStale(community)) await cache.Refresh(community, token);
        return await cache.Load(community, token);
    }

    /// <summary>
    /// Reads <c>Namespace-Name</c> as the catalog's latest version, or <c>Namespace-Name-1.2.3</c> as that version. A
    /// team name may contain dashes, so where the namespace ends is decided by which split the catalog knows.
    /// </summary>
    internal static bool TryResolvePackage(ThunderstoreCatalog catalog, string text, out ThunderstoreVersionRef version, out string problem)
    {
        version = default;
        text = text.Trim();

        if (ThunderstoreDependencyString.TryParse(text, out var dependency))
        {
            ThunderstoreVersionRef resolved;
            try
            {
                resolved = catalog.Resolve(dependency);
            }
            catch (InvalidOperationException e)
            {
                problem = e.Message;
                return false;
            }

            if (catalog.Versions(resolved.Package).Any(known => ThunderstoreVersionNumber.TryParse(known.VersionNumber, out var number) && number == resolved.Version))
            {
                version = resolved;
                problem = string.Empty;
                return true;
            }

            problem = $"Package '{resolved.Package}' has no version {resolved.Version} in community '{catalog.Community}'.";
            return false;
        }

        // Only the candidate splits are wanted here, so the version the struct requires is a placeholder.
        var matches = new ThunderstoreDependencyString(text, default).Candidates().Where(candidate => catalog.TryGet(candidate, out _)).Take(2).ToArray();
        switch (matches.Length)
        {
            case 0:
                problem = $"No package '{text}' in community '{catalog.Community}'.";
                return false;
            case > 1:
                problem = $"'{text}' could be {matches[0]} or {matches[1]}; name a version or use the full Namespace-Name.";
                return false;
        }

        if (catalog.Latest(matches[0], includeDeprecated: true) is not { } latest || !ThunderstoreVersionNumber.TryParse(latest.VersionNumber, out var number))
        {
            problem = $"Package '{matches[0]}' has no active version.";
            return false;
        }

        version = new ThunderstoreVersionRef(matches[0], number);
        problem = string.Empty;
        return true;
    }
}
