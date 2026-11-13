using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.DataModel.Diagnostics;

public partial class BackupSpaceDiagnostics
{
    private const string Source = "SilverModManager.DataModel.BackupSpace";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate BackupsUseALotOfSpace = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Game file backups use a lot of space")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The copies of this game's own files that the app keeps use {Total}.")
        .WithDetails("""
The app copies a game's own files before it overwrites or removes them, so it can put them back. These copies use {Total}, and the app never deletes one by itself.

{Reclaimable}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Total")
            .AddValue<string>("Reclaimable")
        )
        .Finish();
}
