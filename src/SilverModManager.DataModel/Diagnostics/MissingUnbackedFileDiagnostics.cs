using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.DataModel.Diagnostics;

public partial class MissingUnbackedFileDiagnostics
{
    private const string Source = "SilverModManager.DataModel.MissingUnbackedFile";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingUnbackedFile = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("A file is gone and has no backup copy")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The app holds no copy of this file and it is no longer in the game folder. The app cannot put it back; remove it from the loadout or restore the file yourself.")
        .WithDetails("""
{Path} is part of this loadout, but the app holds no copy of it (it was kept in the game folder as it was, or the mod it came from was removed), and it is no longer in the game folder, so there is nothing to restore it from.

Remove it from the loadout, or put the file back in the game folder yourself.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Path")
        )
        .Finish();
}
