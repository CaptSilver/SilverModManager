using System.Collections.Frozen;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine.Morrowind.OpenMw;

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
