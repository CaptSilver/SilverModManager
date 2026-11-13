using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// Says when two active paks are the same module. The mod list names a module once, so the writer leaves the
/// extra copy out; this tells the user which one it kept.
/// </summary>
public sealed class DuplicateModuleEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IGame _game;
    private readonly LarianPakModules _modules;

    public DuplicateModuleEmitter(IGame game, LarianPakModules modules)
    {
        _game = game;
        _modules = modules;
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var variety = _game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        var plan = LarianModListPlan.Build(await _modules.Candidates(loadout, variety, found.Value, cancellationToken));
        foreach (var duplicate in plan.Duplicates)
        {
            yield return Diagnostics.CreateDuplicateModule(
                ModuleName: duplicate.ModuleName,
                KeptPak: duplicate.KeptPak,
                IgnoredPaks: string.Join(", ", duplicate.IgnoredPaks));
        }
    }
}
