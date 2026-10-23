using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.GameInstalled;

/// <summary>
/// Warns while the "Installed by the game" group is switched off. Switching it off only stops the app
/// from listing those items in the game's plugin list; the game can still load anything its own
/// Creation Club list names, so the user should know the switch is not a guarantee.
/// </summary>
public class GameInstalledGroupDisabledEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var group in GameInstalledGroup.FindByInstalledFor(loadout.Db, loadout.Id))
        {
            var groupItem = group.AsLoadoutItemGroup();
            if (groupItem.AsLoadoutItem().IsDisabled is false) continue;

            var names = groupItem.Children
                .Select(item => item.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0) continue;

            var items = string.Join('\n', names.Select(name => $"- {name}"));
            yield return Diagnostics.CreateGameInstalledGroupDisabled(loadout.Installation.Name, names.Length, items);
        }
    }
}
