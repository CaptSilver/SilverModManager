using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// Says what the next apply does about a game update, beside <see cref="BuildDriftEmitter"/>'s report that the build
/// moved. A patched install with a regulation among the mods lists the heal steps; a regulation that changed without a
/// patch is critical whatever the mods are, because nothing may take it as the vanilla one.
/// </summary>
public class HealEmitter(BuildStampStore stamps, LoadoutMergeSources sources) : IFromSoftDiagnosticEmitter
{
    // Compares the live build with the stamp, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (installation.Game is not IFromSoftGame game) yield break;

        var stamped = stamps.Find(loadout.InstallationId);
        if (stamped is null) yield break;

        var live = BuildIdentity.Identify(installation);
        switch (BuildIdentity.Classify(stamped, live))
        {
            case BuildChangeKind.Tampered:
                yield return FromSoftDiagnostics.CreateHealRefused(HealPlan.TamperedRefusal(live, game.Formats));
                break;
            case BuildChangeKind.Patched when ShipsARegulation(loadout, game):
                var steps = string.Join("\n", HealPlan.Plan(stamped, live, game.Formats).Select(s => "- " + s.Detail));
                yield return FromSoftDiagnostics.CreateHealPlan(game.DisplayName, steps);
                break;
        }
    }

    private bool ShipsARegulation(Loadout.ReadOnly loadout, IFromSoftGame game)
        => game.Formats.RegulationFileName is { } name
           && sources.Providers(loadout).Any(p => p.RelPath.Equals(name, StringComparison.OrdinalIgnoreCase));
}
