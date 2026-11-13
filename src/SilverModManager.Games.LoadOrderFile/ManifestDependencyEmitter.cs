using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// A mod of the loadout whose manifest names a dependency the order file the app writes will not list. A mod the game
/// folder or the Steam Workshop already holds satisfies one only when the game lists it.
/// </summary>
public sealed class ManifestDependencyEmitter(IFileStore fileStore, ILogger<GeneratedOrderFile> logger) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (loadout.InstallationInstance.Game is not LoadOrderFileGame game || game.Definition.ManifestReader is null) yield break;

        var definition = game.Definition;
        var managed = await ManagedModList.Read(definition, loadout, fileStore, cancellationToken);
        var listed = (await new GeneratedOrderFile(definition, logger).Order(loadout, managed, cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in managed)
        {
            if (mod.Manifest is not { } manifest) continue;

            var missing = manifest.Dependencies
                .Where(dependency => !listed.Contains(dependency))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0) continue;

            var diagnostic = Diagnostics.CreateMissingDependency(
                Group: mod.Group.ToReference(loadout),
                Mod: mod.Name,
                Missing: string.Join(", ", missing));
            yield return definition.DependenciesAreRequired ? diagnostic with { Severity = DiagnosticSeverity.Critical } : diagnostic;
        }
    }
}
