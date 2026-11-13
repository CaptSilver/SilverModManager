using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.LoadOrderFile;

internal static partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.LoadOrderFile";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate WorkshopDuplicate = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("A mod is installed twice")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Mod} is installed both here and from the Steam Workshop")
        .WithDetails("""
**{Group}** is in this loadout, and the same mod, **{Mod}**, is also subscribed to on the Steam Workshop. The game lists a mod once, so it loads one of the two copies and the other is ignored.

Unsubscribe from the Workshop item, or remove the copy installed here.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Mod")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OrderViolation = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("A mod is in the wrong place in the load order")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Mod} should {Rule} {Target}, but the order puts it the other way round")
        .WithDetails("""
The manifest of **{Mod}** says it should **{Rule}** **{Target}**, and the order file the app writes has them the other way round. The mod may not work as its author meant.

Where to change it: {FixPage}.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Mod")
            .AddValue<string>("Target")
            .AddValue<string>("Rule")
            .AddValue<string>("FixPage")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate Incompatible = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Two mods say they do not work together")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Mod} and {Other} are both enabled, and one says it is incompatible with the other")
        .WithDetails("""
The manifest of **{Mod}**, or of **{Other}**, lists the other as incompatible. The game warns about the pair and the two may break each other.

Disable one of them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Mod")
            .AddValue<string>("Other")
        )
        .Finish();
}
