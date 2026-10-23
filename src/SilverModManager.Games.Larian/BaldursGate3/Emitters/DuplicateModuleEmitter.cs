using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.BaldursGate3.Emitters;

/// <summary>
/// Says when two active paks are the same module. The mod list names a module once, so the writer leaves the
/// extra copy out; this tells the user which one it kept.
/// </summary>
public sealed class DuplicateModuleEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IGame _game;
    private readonly Bg3PakModules _modules;

    public DuplicateModuleEmitter(IGame game, Bg3PakModules modules)
    {
        _game = game;
        _modules = modules;
    }

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var variety = _game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) yield break;

        var plan = Bg3ModListPlan.Build(await _modules.Candidates(loadout, variety, found.Value, cancellationToken));
        foreach (var duplicate in plan.Duplicates)
        {
            yield return Diagnostics.CreateDuplicateModule(
                ModuleName: duplicate.ModuleName,
                KeptPak: duplicate.KeptPak,
                IgnoredPaks: string.Join(", ", duplicate.IgnoredPaks));
        }
    }
}
