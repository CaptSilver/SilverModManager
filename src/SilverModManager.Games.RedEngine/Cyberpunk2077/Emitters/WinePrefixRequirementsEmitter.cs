using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;

using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;

public class WinePrefixRequirementsEmitter(IWineDllOverrideRequirements game, IGameLauncherSkip launcher) : ILoadoutDiagnosticEmitter
{
    private static readonly ImmutableHashSet<string> RequiredWinetricksPackages =
    [
        "d3dcompiler_47",
        "vcrun2022",
    ];

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var dllOverridesInstructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(loadout.InstallationInstance, game.RequiredWineDllOverrides(loadout), launcher.LauncherSkipArguments, cancellationToken: cancellationToken);
        if (dllOverridesInstructions is not null)
        {
            yield return Diagnostics.CreateRequiredWineDllOverrides(dllOverridesInstructions)
                with { Actions = WineDiagnosticHelper.GetWineDllOverridesFixActions(loadout.InstallationInstance) };
        }

        var winetricksInstructions = await WineDiagnosticHelper.GetWinetricksInstructions(loadout.InstallationInstance, RequiredWinetricksPackages, cancellationToken: cancellationToken);
        if (winetricksInstructions is not null) yield return Diagnostics.CreateRequiredWinetricksPackages(winetricksInstructions);
    }
}
