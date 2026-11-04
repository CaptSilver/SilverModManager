using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.DataModel;

internal partial class LoadoutManager
{
    /// <summary>
    /// What other mod managers left in the game folder, read before the loadout or the vanilla baseline exist so a
    /// refusal leaves no rows behind.
    /// </summary>
    private ForeignDeployment[] DetectForeignDeployments(GameInstallation installation)
    {
        var found = new List<ForeignDeployment>();
        foreach (var source in _serviceProvider.GetServices<IForeignDeploymentSource>())
        {
            var deployment = source.Detect(installation);
            if (deployment is null) continue;

            _logger.LogInformation("{Manager} left {Mods} mods ({Files} files) in the folder of {Game}",
                deployment.Manager, deployment.Mods.Count, deployment.Files.Count(), installation.Game.DisplayName);
            found.Add(deployment);
        }

        return found.ToArray();
    }

    private bool WillFirstSync(GameInstallation installation)
    {
        if (!_gameRegistry.TryGetMetadata(installation, out var metadata)) return true;
        if (!GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSynced)) return true;
        return !Loadout.Load(_connection.Db, lastSynced).IsValid();
    }

    /// <summary>
    /// Makes sure the installation has a baseline that does not count the other manager's files as the game's own.
    /// The coordinator leaves them out of every baseline it builds, but one that was built from the folder before
    /// they were there, or before this was known, lists them as vanilla, and the first sync would then keep them as
    /// game files instead of ingesting them. That one is removed and built again. A depot-manifest baseline is
    /// left alone: it lists what the store shipped, so mods are non-vanilla whoever put them there.
    /// </summary>
    private async Task EnsureLocalBaselineWithoutForeignFiles(GameInstallation installation, IReadOnlyList<ForeignDeployment>? foreign, CancellationToken cancellationToken)
    {
        // Handed to every build so none reads the records again; null (nothing was read) leaves a build to read them itself.
        var foreignFiles = foreign?.SelectMany(deployment => deployment.Files).Where(path => path.LocationId == LocationId.Game).ToHashSet();
        var baseline = await _fileHashesService.EnsureLocalBaseline(installation, cancellationToken, foreignFiles);
        if (foreign is null || foreign.Count == 0 || baseline.Rebuilt || baseline.Kind == LocalBaselineKind.DepotManifests) return;

        var gamePath = installation.Locations[LocationId.Game].Path.ToString();
        var foreignPaths = foreignFiles?.Select(path => path.Path).ToHashSet() ?? [];
        var listedAsVanilla = LocalBaseline.FindByGamePath(_fileHashesService.Local, gamePath)
            .Where(local => local.Store == installation.LocatorResult.Store)
            .Any(local => local.Files.Any(file => foreignPaths.Contains(file.Path)));
        if (!listedAsVanilla) return;

        _logger.LogInformation("The baseline of {Game} lists files another mod manager deployed as the game's own, so it is built again without them", installation.Game.DisplayName);
        await _fileHashesService.RemoveLocalBaseline(installation.LocatorResult.Store, gamePath, cancellationToken);
        await _fileHashesService.EnsureLocalBaseline(installation, cancellationToken, foreignFiles);
    }

    /// <summary>
    /// Moves the files of each foreign mod out of the Overrides group, where the first sync put everything on disk that
    /// neither the game nor a mod provides, into a group of their own. Reuses that sync's ingest, so the bytes are
    /// already in the file store and the rows keep their ids.
    /// </summary>
    private async Task<Loadout.ReadOnly> AdoptForeignDeployments(Loadout.ReadOnly loadout, IReadOnlyList<ForeignDeployment> deployments)
    {
        if (deployments.Count == 0) return loadout;

        loadout = loadout.Rebase();
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).FirstOrDefault();
        var rows = overrides.IsValid()
            ? overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath()
                .Where(row => !row.TryGetAsDeletedFile(out _))
                .ToDictionary(row => (GamePath)row.TargetPath)
            : [];
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;

        using var tx = _connection.BeginTransaction();
        var newGroups = new List<LoadoutItemGroupId>();
        var adopted = new List<AdoptedMod>();
        var notAdopted = new List<GamePath>();
        var missing = deployments.SelectMany(d => d.MissingFiles).ToList();
        var replaced = deployments.SelectMany(d => d.Replaced).ToList();

        foreach (var deployment in deployments)
        {
            foreach (var mod in deployment.Mods)
            {
                var movable = new List<LoadoutItemWithTargetPath.ReadOnly>();
                foreach (var path in mod.Files)
                {
                    if (rows.TryGetValue(path, out var row) && synchronizer.CanMoveOverrides(loadout, path)) movable.Add(row);
                    else notAdopted.Add(path);
                }

                if (movable.Count == 0) continue;

                _ = new ForeignDeploymentGroup.New(tx, out var groupEntity)
                {
                    Manager = deployment.Manager,
                    Source = mod.Source,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupEntity)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, groupEntity)
                        {
                            LoadoutId = loadout.Id,
                            Name = mod.DisplayName,
                        },
                    },
                };
                if (mod.NexusModId is { } modId) tx.Add(groupEntity, ForeignDeploymentGroup.NexusModId, modId);
                if (mod.Version is { } version) tx.Add(groupEntity, ForeignDeploymentGroup.Version, version);

                var groupId = LoadoutItemGroupId.From(groupEntity);
                foreach (var row in movable) tx.Add(row.Id, LoadoutItem.Parent, groupId);

                newGroups.Add(groupId);
                adopted.Add(new AdoptedMod(deployment.Manager, mod.DisplayName, movable.Count));
            }
        }

        foreach (var path in notAdopted)
            _logger.LogInformation("{Path} is listed by another mod manager but is not among the files the first sync ingested, so it is left alone", path.Path);

        if (adopted.Count == 0) return loadout;

        tx.Add(new ApplyGroupRulesTxFunc(loadout.LoadoutId, new RuleDelta { NewGroups = [.. newGroups] }));
        var result = await tx.Commit();

        Announce(new ForeignAdoptionResult(adopted, missing, notAdopted, replaced));
        return loadout.Rebase(result.Db);
    }

    private void Announce(ForeignAdoptionResult result)
    {
        var text = result.Describe();
        _logger.LogInformation("{Notice}", text);
        _serviceProvider.GetService<IDesktopNotifier>()?.Notify(result.Headline, result.Body);
    }
}

/// <summary>One mod taken over from another manager.</summary>
internal sealed record AdoptedMod(string Manager, string Name, int FileCount);

/// <summary>What adopting another manager's mods did, in the words the player is shown once.</summary>
internal sealed record ForeignAdoptionResult(IReadOnlyList<AdoptedMod> Adopted, IReadOnlyList<GamePath> MissingFiles, IReadOnlyList<GamePath> NotAdopted, IReadOnlyList<GamePath> ReplacedFiles)
{
    private const int MaxListed = 15;

    private string ManagerNames => string.Join(" and ", Adopted.Select(mod => mod.Manager).Distinct());

    public string Headline
    {
        get
        {
            var managers = ManagerNames;
            return Adopted.Count == 1 ? $"1 mod was adopted from {managers}" : $"{Adopted.Count} mods were adopted from {managers}";
        }
    }

    public string Body
    {
        get
        {
            var lines = Adopted.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).Take(MaxListed).Select(mod => $"- {mod.Name} ({Count(mod.FileCount, "file")})").ToList();
            if (Adopted.Count > MaxListed) lines.Add($"- and {Adopted.Count - MaxListed} more");
            var text = "They are mods in this loadout now; switch one off to take it out of the game folder.\n" + string.Join('\n', lines);
            if (MissingFiles.Count > 0) text += $"\n{Count(MissingFiles.Count, "file")} the manager lists {(MissingFiles.Count == 1 ? "was" : "were")} not on disk and {(MissingFiles.Count == 1 ? "was" : "were")} skipped.";
            if (ReplacedFiles.Count > 0) text += $"\n{Count(ReplacedFiles.Count, "file")} {(ReplacedFiles.Count == 1 ? "was" : "were")} left alone because {ManagerNames} kept the game's original beside {(ReplacedFiles.Count == 1 ? "it" : "them")}.";
            return text;
        }
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public string Describe() => Headline + ":\n" + Body;
}
