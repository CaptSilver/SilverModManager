using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.DataModel.GameInstalled;

public partial class Diagnostics
{
    private const string Source = "SilverModManager.DataModel.GameInstalled";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameInstalledGroupDisabled = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Game-installed content is switched off")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Content installed by the game is switched off")
        .WithDetails("""
{ItemCount} item(s) installed by {Game} itself are switched off in this loadout. {Game} may still load items named in its own Creation Club list, so switching them off here does not guarantee they stay out of the game.

{Items}

To change this, switch the "Installed by the game" group back on from the Mods page.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<int>("ItemCount")
            .AddValue<string>("Items")
        )
        .Finish();
}
