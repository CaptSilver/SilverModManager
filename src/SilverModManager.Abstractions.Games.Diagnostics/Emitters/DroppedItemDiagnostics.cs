using JetBrains.Annotations;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

internal static partial class DroppedItemDiagnostics
{
    private const string Source = "SilverModManager.Abstractions.Games.Diagnostics";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate DroppedItem = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Something was left out")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Item} from {Source} is not used: {Reason}")
        .WithDetails("""
**{Item}** ({Source}) was left out: {Reason}.

Change it on the {Page} page.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Item")
            .AddValue<string>("Source")
            .AddValue<string>("Reason")
            .AddValue<string>("Page")
        )
        .Finish();

    /// <summary>The same card for an item whose remedy is not on any page.</summary>
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate DroppedItemWithoutPage = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Something was left out")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Item} from {Source} is not used: {Reason}")
        .WithDetails("""
**{Item}** ({Source}) was left out: {Reason}.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Item")
            .AddValue<string>("Source")
            .AddValue<string>("Reason")
        )
        .Finish();
}
