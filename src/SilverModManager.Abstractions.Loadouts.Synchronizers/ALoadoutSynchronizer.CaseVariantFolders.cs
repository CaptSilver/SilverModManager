using System.Runtime.InteropServices;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// Refuses a tree whose loadout-written files put two folders differing only in case under one parent, or put
    /// one beside an unmanaged folder of the other spelling, for a Windows game run through Wine or Proton.
    /// Native games and Windows itself keep the two apart or merge them themselves, so only Wine needs the guard.
    /// Vanilla files do not count: a mod folder that differs in case from a game folder is the mod's spelling
    /// and not something this apply can repair, so it is left alone rather than locking every apply.
    /// </summary>
    internal static void RefuseCaseVariantFolders(Dictionary<GamePath, SyncNode> syncTree, GameInstallation installation)
    {
        var runsUnderWine = installation.LocatorResult.WinePrefixPath is not null
                            || (installation.LocatorResult.Platform == OSPlatform.Windows && !OperatingSystem.IsWindows());
        if (!runsUnderWine) return;

        var deployed = syncTree.Where(kv => kv.Value.HaveLoadout && (kv.Value.SourceItemType is LoadoutSourceItemType.Loadout or LoadoutSourceItemType.Intrinsic)).Select(kv => kv.Key);
        var unmanaged = syncTree.Where(kv => kv.Value is { HaveDisk: true, HaveLoadout: false, HavePrevious: false }).Select(kv => kv.Key);
        var pair = CaseVariantFolders.Find(deployed, unmanaged);
        if (pair is not { } found) return;

        throw new SynchronizerPreflightException(ProtonMessages.CaseVariantFolders(installation.Game.DisplayName, found.First.ToString(), found.Second.ToString()));
    }
}
