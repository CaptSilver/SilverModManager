using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Platform.Proton.Steam;

internal static partial class SteamLaunchDiagnostics
{
    private const string Source = "SilverModManager.Platform.Proton";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate SteamSkipsLoadoutLaunch = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Steam's Play Button Skips The Loadout")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("Steam's Play button skips your {GameName} mods")
        .WithDetails("""
Steam starts {GameName} directly, so the loadout '{LoadoutName}' isn't applied and what the app adds when it
launches the game doesn't run. Play from SilverModManager, or let Steam launch the game through the app.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("LoadoutName")
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
Steam starts {GameName} through {ManagerName}, so the loadout '{LoadoutName}' isn't applied.
Play from SilverModManager, or let Steam launch the game through the app. Two managers can't both run the game,
so the fix replaces the other line and keeps a copy of it.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("LoadoutName")
            .AddValue<string>("ManagerName")
        )
        .Finish();
}
