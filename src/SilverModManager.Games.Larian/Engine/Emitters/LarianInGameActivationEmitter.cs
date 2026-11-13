using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// For a game whose mod list the app does not write: tells the player that the paks the loadout deploys still
/// have to be switched on in the game's own Mods menu.
/// </summary>
public sealed class LarianInGameActivationEmitter(string gameName) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (DeploysAPak(loadout)) yield return Diagnostics.CreateEnableModsInGame(GameName: gameName);
    }

    private static bool DeploysAPak(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup()
            .Where(group => group.AsLoadoutItem().IsEnabled())
            .SelectMany(group => group.Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            .Any(file => file.AsLoadoutItemWithTargetPath().TargetPath is { Item2: var location, Item3: var path }
                         && location == LarianConstants.ModsLocationId
                         && path.Extension == LarianConstants.PakFileExtension);
}
