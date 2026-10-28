using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Diagnostics;

/// <summary>
/// Says that the mods.settings on disk was not the one the app wrote and has been replaced by the Load Order's. The
/// notice that backs the card is retracted when the user next drags a folder or flips a switch.
/// </summary>
public sealed class ModsSettingsReplacedEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var notice in ModsSettingsNotices.For(loadout.Db, loadout.LoadoutId))
            yield return WitcherDiagnostics.CreateModsSettingsReplaced(Reason: notice.Summary);
    }
}
