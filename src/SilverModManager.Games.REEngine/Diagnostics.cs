using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.REEngine;

internal static partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.REEngine";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ReFrameworkMissing = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("REFramework is not installed")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Group} needs REFramework, which this loadout does not have")
        .WithDetails("""
**{Group}** is a REFramework script or plugin, and those are only loaded by REFramework. Without it the game starts without them.

Download **`{Release}`** from the REFramework releases on GitHub and install it from your library; keep it enabled.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Release")
        )
        .Finish();
}
