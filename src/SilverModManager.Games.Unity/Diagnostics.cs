using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.Unity;

internal static partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.Unity";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate LoaderMissing = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("The mod loader is not installed")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Group} needs {Loader}, which this loadout does not have")
        .WithDetails("""
**{Group}** is a {Loader} mod, and only {Loader} loads it. Without {Loader} the game starts without it.

Install the package **`{Package}`** from Thunderstore and keep it enabled.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Loader")
            .AddValue<string>("Package")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RuntimeBranchMismatch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("A mod is built for the other scripting backend")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Group} is built for {Built}; this install is {Install}")
        .WithDetails("""
**{Group}** has a library whose name says it is built for **{Built}**, but this install of the game is **{Install}**. The loader will not load it, or will load it and fail.

Find the build of the mod made for {Install}. If the mod only comes for the other backend, the game's Steam beta branches can switch between them: the `alternate` branch is the Mono build of a game that is IL2CPP by default.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Built")
            .AddValue<string>("Install")
        )
        .Finish();
}
