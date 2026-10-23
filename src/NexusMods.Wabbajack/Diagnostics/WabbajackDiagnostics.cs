using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Wabbajack.Diagnostics;

internal static partial class WabbajackDiagnostics
{
    private const string Source = "NexusMods.Wabbajack";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ModlistFilesSkipped = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Modlist Files Were Not Installed")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{SkippedCount} file(s) of {ModlistName} were not installed")
        .WithDetails("""
The install of **{ModlistName}** could not execute {SkippedCount} of the modlist's directives, so those files are not in the loadout. The modlist stays disabled until that is sorted out.

{Paths}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Modlist")
            .AddValue<string>("ModlistName")
            .AddValue<int>("SkippedCount")
            .AddValue<string>("Paths")
        )
        .Finish();
}
