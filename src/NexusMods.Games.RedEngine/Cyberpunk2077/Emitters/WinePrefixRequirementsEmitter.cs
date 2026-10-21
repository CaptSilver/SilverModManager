using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;

using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Generic;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;

public class WinePrefixRequirementsEmitter(IWineDllOverrideRequirements game) : ILoadoutDiagnosticEmitter
{
    private static readonly ImmutableHashSet<string> RequiredWinetricksPackages =
    [
        "d3dcompiler_47",
        "vcrun2022",
    ];

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var dllOverridesInstructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(loadout.InstallationInstance, game.RequiredWineDllOverrides(loadout), cancellationToken: cancellationToken);
        if (dllOverridesInstructions is not null) yield return Diagnostics.CreateRequiredWineDllOverrides(dllOverridesInstructions);

        var winetricksInstructions = await WineDiagnosticHelper.GetWinetricksInstructions(loadout.InstallationInstance, RequiredWinetricksPackages, cancellationToken: cancellationToken);
        if (winetricksInstructions is not null) yield return Diagnostics.CreateRequiredWinetricksPackages(winetricksInstructions);
    }
}
