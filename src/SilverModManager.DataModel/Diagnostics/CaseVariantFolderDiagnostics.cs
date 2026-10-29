using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.DataModel.Diagnostics;

public partial class CaseVariantFolderDiagnostics
{
    private const string Source = "SilverModManager.DataModel.CaseVariantFolder";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate CaseVariantFolders = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Folders that differ only in letter case")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Game} sees only one folder of each pair that differs only in letter case, so the files in the other do not load")
        .WithDetails("""
{Game} runs under Wine, which treats folder names as the same when only their letter case differs. The game sees one folder of each pair below, and the files in the other are lost to it.

{Pairs}

For files the app deploys, there is nothing to do for The Witcher 3: the next Apply merges these folders. For any other game, move the files of one folder of the pair into the other.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Pairs")
        )
        .Finish();
}
