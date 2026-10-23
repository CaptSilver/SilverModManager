using System.Collections.Frozen;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Morrowind.OpenMw;

/// <summary>Warns when native and Flatpak OpenMW both have an openmw.cfg, since the app will not guess between them.</summary>
public class OpenMwDiagnosticEmitter(IOpenMwConfigRoots roots, IFileSystem fileSystem) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (OpenMwConfigLocation.Locate(roots, fileSystem) is OpenMwConfigLocation.Both both)
            yield return Diagnostics.CreateOpenMwConfigAmbiguous(both.First.GetFullPath(), both.Second.GetFullPath());
    }
}
