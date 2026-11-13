using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// A mod the loadout installs that the player is also subscribed to on the Steam Workshop. The order file lists a mod
/// once, so one of the two copies is ignored and the player cannot tell which.
/// </summary>
public sealed class WorkshopDuplicateEmitter(IFileStore fileStore) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (loadout.InstallationInstance.Game is not LoadOrderFileGame game) yield break;

        var gameFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, NexusMods.Paths.RelativePath.Empty));
        var subscribed = game.Definition.ExternalMods.OfType<SteamWorkshopModSource>()
            .SelectMany(source => source.Find(gameFolder))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subscribed.Count == 0) yield break;

        foreach (var mod in await ManagedModList.Read(game.Definition, loadout, fileStore, cancellationToken))
        {
            if (subscribed.Contains(mod.Name))
                yield return Diagnostics.CreateWorkshopDuplicate(Group: mod.Group.ToReference(loadout), Mod: mod.Name);
        }
    }
}
