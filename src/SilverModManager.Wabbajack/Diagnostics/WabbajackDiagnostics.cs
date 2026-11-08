using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Generators.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;

namespace SilverModManager.Wabbajack.Diagnostics;

internal static partial class WabbajackDiagnostics
{
    private const string Source = "SilverModManager.Wabbajack";

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
            .AddValue<MarkdownFragment>("Paths")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ModlistUpdateAvailable = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("A Newer Modlist Version Is Available")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("A newer version of {ModlistName} is available ({InstalledVersion} → {NewVersion}{Download})")
        .WithDetails("""
The gallery lists **{NewVersion}** of **{ModlistName}**; this loadout has **{InstalledVersion}** installed. Open the modlist's page to review the update.

{ListingNote}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Modlist")
            .AddValue<string>("ModlistName")
            .AddValue<string>("InstalledVersion")
            .AddValue<string>("NewVersion")
            .AddValue<string>("Download")
            .AddValue<string>("ListingNote")
        )
        .Finish();
}
