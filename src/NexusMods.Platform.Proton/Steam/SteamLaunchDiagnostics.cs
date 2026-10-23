using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Platform.Proton.Steam;

internal static partial class SteamLaunchDiagnostics
{
    private const string Source = "NexusMods.Platform.Proton";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate SteamSkipsLoadoutLaunch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Steam Does Not Launch The Game Through The App")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("Steam doesn't launch {GameName} through SilverModManager")
        .WithDetails("""
The launch options Steam has stored for {GameName} don't run the loadout '{LoadoutName}'. Steam's own Play
button, and the app's Play when it hands the game to Steam, start the game without what the app adds when it launches it:

{Skipped}

"Use this loadout from Steam" writes a launch line that runs the game through the app. Steam has to be closed
for that, because it rewrites its config when it exits.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("LoadoutName")
            .AddValue<string>("Skipped")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OtherManagerLaunchesGame = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Steam Launches The Game Through Another Mod Manager")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Steam launches {GameName} through {ManagerName}, not SilverModManager")
        .WithDetails("""
The launch options Steam has stored for {GameName} start {ManagerName}, which runs the game itself. Steam's own
Play button, and the app's Play when it hands the game to Steam, start the game without what the loadout
'{LoadoutName}' relies on:

{Skipped}

"Use this loadout from Steam" can replace that line with the one for this loadout. The old line is kept in a
record file, and the dialog asks before replacing it. Two managers cannot both run the game.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("LoadoutName")
            .AddValue<string>("ManagerName")
            .AddValue<string>("Skipped")
        )
        .Finish();
}
