using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.Generic.Diagnostics;

internal static partial class Diagnostics
{
    private const string Source = "NexusMods.Games.Generic";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingProtonPrefix = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Proton Prefix")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{GameName} has no Proton prefix yet")
        .WithDetails("""
{Explanation}

The game's settings and save files live inside that prefix, so the loadout cannot be applied until it
exists. The app does not fall back to another folder.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Explanation")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ProtonPrefixNotLoaded = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Proton Prefix Not Loaded")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The Proton prefix of {GameName} was created after the app started")
        .WithDetails("""
{GameName} was launched after the app started, so the app has not picked up its Proton prefix yet.
Restart the app so tools that run inside the prefix can find it.

Adding and removing mods is not affected.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate HostCopyOfGameFolder = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Folder Also Exists Outside The Proton Prefix")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{GameName} has a copy of its '{Location}' folder outside the Proton prefix")
        .WithDetails("""
A copy of the `{Location}` folder exists on the host at `{HostPath}`.
{GameName} runs under Proton and uses `{PrefixPath}`.

The app only works in the folder the game uses and leaves the host copy alone. It usually comes from running
the game natively or from an older version of the app. Settings changed in the host copy have no effect on the game.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Location")
            .AddValue<string>("HostPath")
            .AddValue<string>("PrefixPath")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate LoadoutItemsTargetProtectedPath = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("Files Target A Protected Path")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' has files the app will not deploy")
        .WithDetails("""
**{Group}** contains files that target paths the app does not write to, such as the game's save folder, so these
files are left out when the loadout is applied:

{Paths}

The files stay in the loadout. Deploying them would put files on disk that the app could not remove again when
the loadout is switched or the game is unmanaged, and could overwrite the player's own saves.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Paths")
        )
        .Finish();
}
