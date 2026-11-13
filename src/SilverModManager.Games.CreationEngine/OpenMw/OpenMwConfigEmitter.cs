using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.Bethesda.Loot.Conditions;
using SilverModManager.Games.CreationEngine.SortOrder;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Reads openmw.cfg for what stops OpenMW or leaves a mod unused: <c>content=</c> lines for files no data directory
/// has (OpenMW refuses to start on one), and deployed archives nothing registers with a <c>fallback-archive=</c> line.
/// It also reads settings.cfg for the groundcover switch, which silences every <c>groundcover=</c> line while it is
/// off. The files are only read; the user decides what to change.
/// </summary>
public class OpenMwConfigEmitter(OpenMw game) : ILoadoutDiagnosticEmitter
{
    // The engine supplies this one; no data directory holds a file for it.
    private const string BuiltInScripts = "builtin.omwscripts";

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (GroundcoverOff(loadout) is { } groundcoverOff) yield return groundcoverOff;

        var file = installation.Locations.ToAbsolutePath(OpenMwConstants.OpenMwConfigFile);
        if (OpenMwPaths.ReadConfig(file) is not { } text) yield break;

        var directories = game.PluginDirectories(installation);
        var dangling = OpenMwContentWriter.ContentNames(text)
            .Concat(OpenMwConfigValues.Values(text, "groundcover"))
            .Where(name => !name.Equals(BuiltInScripts, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !ConditionPaths.IsPlainFileName(name) || !directories.Any(directory => (directory / name).FileExists))
            .ToList();
        if (dangling.Count > 0)
            yield return Diagnostics.CreateOpenMwDanglingContent(string.Join(", ", dangling), file.GetFullPath());

        var registered = OpenMwConfigValues.Values(text, "fallback-archive").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unregistered = DeployedArchives(loadout)
            .Where(name => !registered.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unregistered.Count > 0)
            yield return Diagnostics.CreateOpenMwUnregisteredArchive(string.Join(", ", unregistered));
    }

    private Diagnostic? GroundcoverOff(Loadout.ReadOnly loadout)
    {
        var variety = game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();
        var groundcover = variety.StoredOrder(loadout).Where(row => row.IsGroundcover && row.IsEnabled).Select(row => row.Name).ToList();
        if (groundcover.Count == 0) return null;

        var settings = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(OpenMwConstants.OpenMwConfig, "settings.cfg"));
        if (settings.FileExists && OpenMwSettings.GroundcoverEnabled(File.ReadAllText(settings.GetFullPath()))) return null;

        return Diagnostics.CreateOpenMwGroundcoverOff(string.Join(", ", groundcover), settings.GetFullPath());
    }

    private IEnumerable<string> DeployedArchives(Loadout.ReadOnly loadout) => LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId)
        .OfTypeLoadoutItemWithTargetPath()
        .OfTypeLoadoutFile()
        .Where(item => item.AsLoadoutItemWithTargetPath().AsLoadoutItem().IsEnabled())
        .Select(item => (GamePath)item.AsLoadoutItemWithTargetPath().TargetPath)
        .Where(path => path.Parent == game.DataPath && path.Extension == KnownCEExtensions.BSA)
        .Select(path => path.FileName.ToString());
}
