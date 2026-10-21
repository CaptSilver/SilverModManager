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

If the mod is meant to ship save files, right-click it in the loadout and turn on **Allow save files**. Its files
then deploy to the save folder, except where a file of the player's own is already there.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("Paths")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate DanglingUserDataLink = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 5))
        .WithTitle("Save Or Settings Folder Is A Broken Link")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{GameName} has a folder that links to a folder that no longer exists")
        .WithDetails("""
`{Path}` is a link to `{Target}`, which does not exist. {GameName} cannot read or write there, so it may report a
corrupted profile and show no saves.

This happens when another tool replaces the folder with a link into its own storage and is then removed. That tool
usually keeps the original folder next to the link under a backup name.

{Backups}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Path")
            .AddValue<string>("Target")
            .AddValue<string>("Backups")
        )
        .Finish();
}
