using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.Witcher.Witcher3;

internal static partial class WitcherDiagnostics
{
    private const string Source = "SilverModManager.Games.Witcher.Witcher3";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ModsSettingsReplacedTemplate = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("mods.settings was replaced")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The mods.settings in Documents/The Witcher 3 was replaced by the one built from your Load Order, because {Reason}")
        .WithDetails("""
The Witcher 3 reads **mods.settings** to decide which Mods folders load and in what order. The app writes it from your Load Order on every Apply, so the copy in Documents/The Witcher 3 was replaced, because {Reason}.

To keep a different order or switch, set it on the Load Order page.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Reason")
        )
        .Finish();
}
