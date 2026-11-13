using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Morrowind;

/// <summary>
/// Tells a loadout that used to write openmw.cfg that it no longer does, and which of its plugins the file still
/// lists, since removing those from the loadout leaves the lines pointing at nothing.
/// </summary>
public class MorrowindOpenMwRetiredEmitter(Morrowind game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();

        // Only a loadout whose last apply wrote the file has anything to be told.
        var previous = loadout.InstallationInstance.GetGame().Synchronizer.GetPreviouslyAppliedDiskState(loadout.Installation);
        if (!previous.Any(entry => entry.Path == OpenMwConstants.OpenMwConfigFile)) yield break;

        yield return Diagnostics.CreateMorrowindOpenMwRetired(string.Join(", ", ListedPlugins(loadout)) is { Length: > 0 } listed ? listed : "none");
    }

    private IEnumerable<string> ListedPlugins(Loadout.ReadOnly loadout)
    {
        var installation = loadout.InstallationInstance;
        if (!installation.Locations.TryGetValue(OpenMwConstants.OpenMwConfig, out var folder)
            || OpenMwPaths.ReadConfig(folder.Path / OpenMwConstants.ConfigFileName) is not { } text)
            return [];

        var listed = OpenMwContentWriter.ContentNames(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Where(item => item.AsLoadoutItemWithTargetPath().AsLoadoutItem().IsEnabled())
            .Select(item => (GamePath)item.AsLoadoutItemWithTargetPath().TargetPath)
            .Where(path => path.Parent == game.DataPath && game.PluginExtensions.Contains(path.Extension))
            .Select(path => path.FileName.ToString())
            .Where(listed.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
