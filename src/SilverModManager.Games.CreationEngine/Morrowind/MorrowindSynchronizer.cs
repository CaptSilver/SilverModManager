using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.CreationEngine.OpenMw;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Morrowind;

/// <summary>
/// The Morrowind row used to write openmw.cfg when it found one, so a loadout from that time has the file in its
/// last applied disk state. With the file no longer written, the sync would read that as a file to delete.
/// </summary>
public class MorrowindSynchronizer(IServiceProvider provider, Morrowind game) : BethesdaSynchronizer(provider, game)
{
    // Every action on openmw.cfg is dropped when the sync runs, so the file stays exactly as the user and OpenMW left it.
    protected override bool IsNeverWritten(GamePath path) => path == OpenMwConstants.OpenMwConfigFile;

    // The Apply preview and the status check read the tree before that drop, and would list a delete of the file
    // for ever. The file stays in the recorded state: the retirement notice reads it from there.
    protected override void AdjustActions(Dictionary<GamePath, SyncNode> tree)
    {
        base.AdjustActions(tree);

        ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, OpenMwConstants.OpenMwConfigFile);
        if (Unsafe.IsNullRef(ref node) || node.HaveLoadout || !node.HavePrevious) return;

        node.Actions = Actions.DoNothing;
    }
}
