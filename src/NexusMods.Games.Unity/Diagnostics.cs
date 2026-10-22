using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.Unity;

internal static partial class Diagnostics
{
    private const string Source = "NexusMods.Games.Unity";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate BepInExMissing = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("BepInEx is not installed")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Group} needs BepInEx, which this loadout does not have")
        .WithDetails("""
**{Group}** is a BepInEx plugin, and plugins are only loaded by BepInEx. Without it the game starts without them.

Install the package **`{Package}`** from Thunderstore and keep it enabled.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Package")
        )
        .Finish();
}
