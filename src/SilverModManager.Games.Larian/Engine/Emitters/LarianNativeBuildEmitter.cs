using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Emitters;

/// <summary>
/// For a game whose Linux depot the app does not manage: a Steam install of that depot is located as a Windows
/// build whose Proton prefix does not exist, and this says what to do about it.
/// </summary>
public sealed class LarianNativeBuildEmitter(string gameName) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var result = loadout.InstallationInstance.LocatorResult;

        // The same test the Proton prefix diagnostic uses for a prefix that is not there yet.
        if (result.Compatibility is { Kind: not CompatibilityLayerKind.SteamProton }) yield break;
        if (result.WinePrefixPath is { } prefix && !ProtonPrefix.IsReady(prefix))
            yield return Diagnostics.CreateNativeBuildUnsupported(GameName: gameName);
    }
}
