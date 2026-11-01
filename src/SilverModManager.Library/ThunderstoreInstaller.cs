using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Library;

/// <summary>
/// <see cref="IThunderstoreInstaller"/> over <see cref="IThunderstoreSource"/> and the loadout manager.
/// </summary>
internal sealed class ThunderstoreInstaller(
    IServiceProvider provider,
    IConnection connection,
    IEnumerable<IGameData> games,
    IThunderstoreApi api,
    IThunderstoreCatalogSource catalogs,
    ILogger<ThunderstoreInstaller> logger) : IThunderstoreInstaller
{
    public bool TryGetGame(LoadoutId loadoutId, [NotNullWhen(true)] out IThunderstoreGame? game, out string problem)
    {
        var loadout = Loadout.Load(connection.Db, loadoutId);
        var data = GameInstallMetadata.Game.TryGetValue(loadout.Installation, out var gameId)
            ? games.FirstOrDefault(candidate => candidate.GameId == gameId)
            : null;

        game = data is null ? null : ThunderstoreGame.For(data);
        problem = game is null
            ? $"{data?.DisplayName ?? "The loadout's game"} has no Thunderstore support: its mods do not come from Thunderstore."
            : string.Empty;
        return game is not null;
    }

    public async ValueTask<IReadOnlyList<string>> CommunitiesOf(ThunderstorePackageRef package, CancellationToken cancellationToken)
    {
        var detail = await api.FetchPackage(package, cancellationToken);
        return detail?.CommunityListings?.Select(listing => listing.Community).ToArray() ?? [];
    }

    public async ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>> InstallAsync(ThunderstoreVersionRef root, LoadoutId loadoutId, CancellationToken cancellationToken)
    {
        var game = GameOfLoadout(loadoutId);
        return await Install(root, loadoutId, game, await PolicyFor(root, game, cancellationToken), cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>> InstallAsync(ThunderstoreVersionRef root, LoadoutId loadoutId, DependencyPolicy policy, CancellationToken cancellationToken)
        => await Install(root, loadoutId, GameOfLoadout(loadoutId), policy, cancellationToken);

    private async ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>> Install(ThunderstoreVersionRef root, LoadoutId loadoutId, IThunderstoreGame game, DependencyPolicy policy, CancellationToken cancellationToken)
    {
        var inLibraryBefore = ThunderstoreLibraryFile.All(connection.Db).Select(file => file.Id).ToHashSet();
        var request = new ThunderstoreInstallRequest(
            root,
            game,
            WithDependencies: true,
            policy,
            ExpectedSha256: null,
            ThunderstoreLoadoutPackages.Installed(connection.Db, loadoutId));

        // The downloads are jobs, so the spine's download indicator shows their progress.
        var files = await provider.GetRequiredService<IThunderstoreSource>().AddAsync(request, cancellationToken);

        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        var outcomes = new List<ThunderstoreInstallOutcome>(files.Count);
        foreach (var file in files)
        {
            var item = file.AsLibraryItem();
            var outcome = await ThunderstoreLoadoutPackages.InstallOrEnable(connection, item, loadoutId, async () => await loadoutManager.InstallItem(item, loadoutId));
            outcomes.Add(new ThunderstoreInstallOutcome(VersionOf(file), item, inLibraryBefore.Contains(file.Id) is false, outcome));
        }

        return outcomes;
    }

    public async ValueTask<ModUpdateOutcome> UpdateAsync(ThunderstoreVersionRef newest, LibraryItem.ReadOnly current, CancellationToken cancellationToken)
    {
        if (current.TryGetAsLibraryFile(out var currentFile) is false || currentFile.TryGetAsThunderstoreLibraryFile(out var currentPackage) is false)
            throw new InvalidOperationException($"{current.Name} is not a Thunderstore package.");

        var game = GameOfCommunity(currentPackage.Community);
        var policy = await PolicyFor(newest, game, cancellationToken);
        var libraryBefore = ThunderstoreLibraryFile.All(connection.Db).Select(file => file.Id).ToHashSet();

        var downloaded = new Dictionary<EntityId, LibraryItem.ReadOnly>();
        LibraryItem.ReadOnly? newRoot = null;

        async Task AddNewest(LoadoutId? loadoutId)
        {
            // The root is asked for even when the loadout holds this version already: it is what the old one is
            // replaced with, and a loadout that holds a newer version would otherwise leave it out of the answer.
            var installed = loadoutId is { } id
                ? ThunderstoreLoadoutPackages.Installed(connection.Db, id)
                : new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>();
            installed.Remove(newest.Package);
            var request = new ThunderstoreInstallRequest(newest, game, WithDependencies: true, policy, ExpectedSha256: null, installed);

            var files = await provider.GetRequiredService<IThunderstoreSource>().AddAsync(request, cancellationToken);
            foreach (var file in files)
            {
                var item = file.AsLibraryItem();
                if (libraryBefore.Contains(file.Id) is false) downloaded[file.Id] = item;
                if (VersionOf(file) == newest) newRoot = item;
                else if (loadoutId is { } target) await LinkDependency(item, target);
            }
        }

        // Only the loadouts the replace below will touch need what the new version depends on.
        var replacedLinks = LinksReplacedFor(current).ToArray();
        var loadouts = replacedLinks.Select(static link => link.AsLoadoutItem().LoadoutId).Distinct().ToArray();
        foreach (var loadoutId in loadouts) await AddNewest(loadoutId);
        if (loadouts.Length == 0) await AddNewest(null);

        var root = newRoot ?? throw new InvalidOperationException($"{newest} was not added to the library.");
        downloaded[root.Id] = root;

        // A loadout that already links the new version only loses the old one: replacing would install a second copy.
        var linkedBefore = LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, root).Select(static link => link.Id).ToHashSet();
        var alreadyThere = LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, root).Select(static link => link.AsLoadoutItem().LoadoutId).ToHashSet();
        var result = LibraryItemReplacementResult.Success;
        try
        {
            var superseded = replacedLinks
                .Where(link => alreadyThere.Contains(link.AsLoadoutItem().LoadoutId))
                .Select(static link => link.AsLoadoutItemGroup().LoadoutItemGroupId)
                .ToArray();
            if (superseded.Length > 0) await provider.GetRequiredService<ILoadoutManager>().RemoveItems(superseded);
        }
        catch (Exception e) when (cancellationToken.IsCancellationRequested is false)
        {
            logger.LogWarning(e, "Removing {Package} from the loadouts that already have {Newest} failed", current.Name, newest);
            result = LibraryItemReplacementResult.FailedUnknownReason;
        }

        // The replace installs the new version as a group of its own, which starts enabled, so a version the user had
        // switched off is switched off again afterwards.
        var switchedOff = loadouts
            .Where(id => alreadyThere.Contains(id) is false)
            .Where(id => replacedLinks.Where(link => link.AsLoadoutItem().LoadoutId == id).All(static link => link.AsLoadoutItem().IsDisabled))
            .ToHashSet();

        if (result == LibraryItemReplacementResult.Success)
        {
            result = await provider.GetRequiredService<ILibraryService>()
                .ReplaceLinkedItemsInAllLoadouts(current, root, new ReplaceLibraryItemOptions { IgnoreReadOnlyCollections = true });
        }
        if (result == LibraryItemReplacementResult.Success) await SwitchOff(root, switchedOff, linkedBefore);

        return ModUpdateOutcome.Nothing with
        {
            Downloaded = [.. downloaded.Values],
            Replacements = [new ModUpdateReplacement(current, root, result)],
        };
    }

    private async Task LinkDependency(LibraryItem.ReadOnly item, LoadoutId loadoutId)
    {
        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        await ThunderstoreLoadoutPackages.InstallOrEnable(connection, item, loadoutId, async () => await loadoutManager.InstallItem(item, loadoutId));
    }

    // The links a replace that skips read-only collections would change.
    private IEnumerable<LibraryLinkedLoadoutItem.ReadOnly> LinksReplacedFor(LibraryItem.ReadOnly item)
        => LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, item)
            .Where(static link => link.AsLoadoutItem().Parent.ToCollectionGroup().IsReadOnly is false);

    private async Task SwitchOff(LibraryItem.ReadOnly item, HashSet<LoadoutId> loadouts, HashSet<EntityId> linkedBefore)
    {
        using var tx = connection.BeginTransaction();
        foreach (var link in LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, item))
        {
            if (linkedBefore.Contains(link.Id) || loadouts.Contains(link.AsLoadoutItem().LoadoutId) is false) continue;
            tx.Add(link.Id, LoadoutItem.Disabled, Null.Instance);
        }
        await tx.Commit();
    }

    private IThunderstoreGame GameOfCommunity(string community)
        => ThunderstoreGame.ForCommunity(games, community) ?? throw new InvalidOperationException(ThunderstoreGame.NoGameFor(community));

    // A modpack lists the exact versions it was tested with; anything else follows the newest ones.
    private async Task<DependencyPolicy> PolicyFor(ThunderstoreVersionRef root, IThunderstoreGame game, CancellationToken cancellationToken)
    {
        var community = game.ThunderstoreCommunity;
        if (catalogs.IsStale(community))
        {
            // A failed refresh leaves the previous listing loadable, so offline still reads the cached one.
            try
            {
                await catalogs.Refresh(community, cancellationToken);
            }
            catch (Exception e) when (cancellationToken.IsCancellationRequested is false)
            {
                logger.LogWarning(e, "Refreshing the Thunderstore catalog for {Community} failed; using the cached one", community);
            }
        }

        var catalog = await catalogs.Load(community, cancellationToken);
        return catalog.Categories(root.Package).Contains("Modpacks") ? DependencyPolicy.Exact : DependencyPolicy.Latest;
    }

    private IThunderstoreGame GameOfLoadout(LoadoutId loadoutId)
        => TryGetGame(loadoutId, out var game, out var problem) ? game : throw new InvalidOperationException(problem);

    private static ThunderstoreVersionRef VersionOf(LibraryFile.ReadOnly file)
        => file.TryGetAsThunderstoreLibraryFile(out var package)
            && ThunderstorePackageRef.TryCreate(package.Namespace, package.Name, out var reference)
            && ThunderstoreVersionNumber.TryParse(package.VersionNumber, out var version)
                ? new ThunderstoreVersionRef(reference, version)
                : throw new InvalidOperationException($"{file.FileName} is not a Thunderstore package file.");
}
