using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Abstractions.Games.Merge;

internal static partial class MergeDiagnostics
{
    private const string Source = "SilverModManager.Abstractions.Games.Merge";

    [DiagnosticTemplate]
    internal static IDiagnosticTemplate NotContested = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Bundles are not merged")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Path} ships in {Mods}; where they contain the same file the higher-priority mod's copy is used")
        .WithDetails("""
**{Path}** ships in {Mods}. The game loads every mod's copy and the app does not merge them, so where they contain the same file the higher-priority mod's copy is used.

To change which mod that is, reorder them on the {Page} page.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Path")
            .AddValue<string>("Mods")
            .AddValue<string>("Page")
        )
        .Finish();

    /// <summary>One card for the merged files that had no trustworthy original copy for the same reason.</summary>
    [DiagnosticTemplate]
    internal static IDiagnosticTemplate UntrustedOriginal = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Merged without the game's own copy")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Subject}: {Cause}")
        .WithDetails("""
**{Subject}** could not be used as the original of the merge: {Cause}.

These files were merged two-way: the first mod's copy is the result and what the other mods changed is dropped, which can break the result.

{Entries}

{Advice}.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Subject")
            .AddValue<string>("Cause")
            .AddValue<string>("Entries")
            .AddValue<string>("Advice")
        )
        .Finish();
}
