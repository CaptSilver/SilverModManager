using System.Runtime.CompilerServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Explains the problems a game running under Proton can have with its prefix, and the loadout items the
/// synchronizer will not deploy.
/// </summary>
public class ProtonPrefixEmitter(IFileSystem hostFileSystem) : ILoadoutDiagnosticEmitter
{
    /// <summary>
    /// Listing every path of a mod that ships hundreds of files would bury the sentence that matters.
    /// </summary>
    private const int MaxListedPaths = 10;

    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        foreach (var diagnostic in DiagnoseInstallation(installation, hostFileSystem))
            yield return diagnostic;

        var synchronizer = installation.GetGame().Synchronizer;
        foreach (var diagnostic in DiagnoseProtectedItems(loadout, synchronizer))
            yield return diagnostic;
    }

    /// <summary>
    /// Diagnoses the prefix of a game installation: a missing one, one the app has not picked up, and copies
    /// of the game's folders on the host that the game does not use.
    /// </summary>
    /// <remarks>
    /// The prefix is checked on disk every time. The located installation is cached for the lifetime of the
    /// app, so it cannot tell that the prefix was created or deleted since.
    /// </remarks>
    public static IEnumerable<Diagnostic> DiagnoseInstallation(GameInstallation installation, IFileSystem hostFileSystem)
    {
        var result = installation.LocatorResult;
        if (result.WinePrefixPath is not { } prefix) yield break;

        // Every check below is about Steam's Proton prefix, down to the wording. A game found in a plain Wine
        // or Heroic prefix has no Steam compatibility data to load and never had a Proton prefix to miss.
        if (result.Compatibility is { Kind: not CompatibilityLayerKind.SteamProton }) yield break;

        var gameName = installation.Game.DisplayName;
        if (!ProtonPrefix.IsReady(prefix))
        {
            yield return Diagnostics.CreateMissingProtonPrefix(gameName, ProtonMessages.MissingPrefix(gameName, prefix));
            yield break;
        }

        if (result.LinuxCompatabilityDataProvider is null)
            yield return Diagnostics.CreateProtonPrefixNotLoaded(gameName);

        // Asked with the host file system, the game reports where its native build would keep each folder.
        var hostLocations = installation.Game.GetLocations(hostFileSystem, result);
        foreach (var (locationId, descriptor) in installation.Locations)
        {
            if (locationId == LocationId.Game) continue;
            if (!hostLocations.TryGetValue(locationId, out var hostPath)) continue;
            if (hostPath.GetFullPath() == descriptor.Path.GetFullPath() || !hostPath.DirectoryExists()) continue;

            yield return Diagnostics.CreateHostCopyOfGameFolder(gameName, locationId.ToString(), hostPath.GetFullPath(), descriptor.Path.GetFullPath());
        }
    }

    /// <summary>
    /// One diagnostic per enabled group that has files the synchronizer leaves out because they sit on a
    /// protected path.
    /// </summary>
    private static IEnumerable<Diagnostic> DiagnoseProtectedItems(Loadout.ReadOnly loadout, ILoadoutSynchronizer synchronizer)
    {
        var skippedByGroup = new Dictionary<LoadoutItemGroupId, (LoadoutItemGroup.ReadOnly Group, List<GamePath> Paths)>();

        foreach (var item in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath())
        {
            var loadoutItem = item.AsLoadoutItem();
            var path = (GamePath)item.TargetPath;
            if (!loadoutItem.HasParent() || !loadoutItem.IsEnabled()) continue;
            if (!synchronizer.IsSkippedAsProtected(loadoutItem, path)) continue;

            var group = loadoutItem.Parent;
            if (!skippedByGroup.TryGetValue(group.LoadoutItemGroupId, out var entry))
                skippedByGroup[group.LoadoutItemGroupId] = entry = (group, []);
            entry.Paths.Add(path);
        }

        foreach (var (group, paths) in skippedByGroup.Values)
            yield return Diagnostics.CreateLoadoutItemsTargetProtectedPath(group.ToReference(loadout), FormatPaths(paths));
    }

    private static string FormatPaths(List<GamePath> paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths.OrderBy(p => p.ToString(), StringComparer.Ordinal).Take(MaxListedPaths))
            sb.Append("* `").Append(path).AppendLine("`");

        if (paths.Count > MaxListedPaths)
            sb.Append("* and ").Append(paths.Count - MaxListedPaths).AppendLine(" more");

        return sb.ToString();
    }
}
