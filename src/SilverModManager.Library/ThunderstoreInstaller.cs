using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
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
