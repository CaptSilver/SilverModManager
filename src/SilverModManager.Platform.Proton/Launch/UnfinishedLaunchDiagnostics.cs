using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The summaries carry the whole sentence, which <see cref="UnfinishedLaunchEmitter"/> words; the details say what to do about it.
/// </summary>
internal static partial class UnfinishedLaunchDiagnostics
{
    private const string Source = "SilverModManager.Platform.Proton";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameRunningAfterUnfinishedLaunch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("A Game Is Running And Its Last Session Is Not Finished")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Message}")
        .WithDetails("""
The app takes a session's changes into the loadout once the game has closed. A game that is still running holds that up,
and the loadout stays applied to the game folder meanwhile. Quit the game, or let the app stop it and finish the session.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Message")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ToolOpenAfterUnfinishedLaunch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("A Tool Holds Up Finishing The Last Session")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Message}")
        .WithDetails("""
A tool the app started is still open in the game's prefix. The app leaves it alone and takes the last session's changes
into the loadout once it is closed.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Message")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate SteamUpdatingAfterUnfinishedLaunch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 9))
        .WithTitle("Steam Is Updating A Game With An Unfinished Session")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Message}")
        .WithDetails("""
Steam changes the game folder while it updates, so the app waits before taking the last session's changes into the loadout.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Message")
        )
        .Finish();
}
