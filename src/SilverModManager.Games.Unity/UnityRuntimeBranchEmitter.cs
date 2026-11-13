using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.Unity.MelonLoader;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>
/// A MelonLoader mod whose DLL says it is built for the other scripting backend than the install has. Reported once per
/// group. Thunderstore's own IL2CPP/Mono package categories are not read; the file name is what the installer also goes by.
/// </summary>
public class UnityRuntimeBranchEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (loadout.InstallationInstance.Game is not IUnityGame { Loader: MelonLoaderLoader loader }) yield break;

        var install = UnityRuntimeBranch.Of(loadout.InstallationInstance, loader.Definition);
        if (install == UnityRuntime.Unknown) yield break;

        var reported = new HashSet<EntityId>();
        foreach (var (path, group) in loader.EnabledModLibraries(loadout))
        {
            var built = UnityRuntimeBranch.OfFileName(path.FileName);
            if (built == UnityRuntime.Unknown || built == install) continue;
            if (!reported.Add(group.Id)) continue;

            yield return Diagnostics.CreateRuntimeBranchMismatch(Group: group.ToReference(loadout), Built: Name(built), Install: Name(install));
        }
    }

    private static string Name(UnityRuntime runtime) => runtime == UnityRuntime.Il2Cpp ? "IL2CPP" : "Mono";
}
