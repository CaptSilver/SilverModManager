using System.Runtime.InteropServices;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

public partial class ALoadoutSynchronizer
{
    /// <summary>
    /// Refuses a tree that would put two folders differing only in case under one parent, for a Windows game
    /// run through Wine or Proton. Native games and Windows itself keep the two apart or merge them
    /// themselves, so only Wine needs the guard.
    /// </summary>
    private static void RefuseCaseVariantFolders(Dictionary<GamePath, SyncNode> syncTree, GameInstallation installation)
    {
        var runsUnderWine = installation.LocatorResult.WinePrefixPath is not null
                            || (installation.LocatorResult.Platform == OSPlatform.Windows && !OperatingSystem.IsWindows());
        if (!runsUnderWine) return;

        var pair = CaseVariantFolders.Find(syncTree.Where(kv => kv.Value.HaveLoadout).Select(kv => kv.Key));
        if (pair is not { } found) return;

        throw new SynchronizerPreflightException(ProtonMessages.CaseVariantFolders(installation.Game.DisplayName, found.First.ToString(), found.Second.ToString()));
    }
}
