using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Values;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.FromSoft;

internal static partial class FromSoftDiagnostics
{
    internal const string Source = "NexusMods.Games.FromSoft";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate PackageCollision = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Mods provide the same file")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Packages} provide {Path}")
        .WithDetails("""
More than one mod provides the file **{Path}**: {Packages}.{CaseNote}

The game loads one of them and ignores the rest, and which one wins is not something the app can promise. The
mods are not merged. Disable or remove all but one of them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Packages")
            .AddValue<string>("Path")
            .AddValue<string>("CaseNote")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OverlayCollision = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Mods provide the same configuration file")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Mods} provide {Path} in the game's configuration layer")
        .WithDetails("""
More than one mod puts a file at **{Path}** in the layer that is placed over the game folder for a session: {Mods}.

Only the file that is written last ends up there, and the others are lost without any message. Disable or remove
all but one of them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Mods")
            .AddValue<string>("Path")
        )
        .Finish();
}
