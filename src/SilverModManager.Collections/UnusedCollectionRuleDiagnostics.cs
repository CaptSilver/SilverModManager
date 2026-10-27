using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Collections;

internal static partial class UnusedCollectionRuleDiagnostics
{
    private const string Source = "SilverModManager.Collections";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OptionalModRulesNotUsed = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("An optional mod's ordering rules are not used")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Mod} from {Collection} is not installed, so the collection's ordering rules for it do not apply")
        .WithDetails("""
**{Mod}** is not installed, so the ordering rules {Collection} sets for it have nothing to order.

Install it from the collection page to use them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Mod")
            .AddValue<string>("Collection")
        )
        .Finish();
}
