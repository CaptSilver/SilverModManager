using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Generators.Diagnostics;
using NexusMods.Paths;

namespace SilverModManager.Games.FileHashes;

public partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.FileHashes";

    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate GameFilesDoNotHaveSource = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Game files are not backed up")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("There are {FileCount} game files that do not have a backed up source")
        .WithDetails("""
We've detected that there are {FileCount} game files, totaling {Size}, that do not have a backed up source. This is not inherently a problem, and we will back up any files we need to replace. But any
changes made by you or a game store (such as GOG Galaxy, Steam, etc.) may render this loadout inoperable if these files are not first backed up. 
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<Size>("Size")
            .AddValue<int>("FileCount")
        )
        .Finish();
    
    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate UndeployableLoadoutDueToMissingGameFiles = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Missing Game Files")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Loadout can't be applied due to {FileCount} missing game file(s) with no valid source")
        .WithDetails("""
The loadout is based on {Game} v{Version} but there are {FileCount} file(s) missing from the game installation. Unless these file(s) are restored, the loadout cannot be applied. 

## How to Resolve
1. Open the {Store} launcher 
2. Verify or repair the game files
3. Close and reopen the app

## Technical Details
While checking the contents of the game folder against the file list index for {Game} (v{Version}) from {Store}, {FileCount} file(s) - totalling {Size} - could not be located in the game folder, have not been backed up by the app or cannot be fetched from {Store} automatically.

Without all the required base game files, the loadout cannot be applied.

The files with issues:

{Files}

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<Size>("Size")
            .AddValue<int>("FileCount")
            .AddValue<string>("Files")
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("Version")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameFilesDifferFromTheAppsFileData = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("Game File Data Mismatch")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Loadout can't be applied: the app's file data for this build does not match {FileCount} game file(s) on disk")
        .WithDetails("""
The loadout is based on {Game} v{Version}. {FileCount} file(s), totalling {Size}, are in the game folder, but their contents are not what the app's file data lists for this build, and the app has no copy of the listed contents to put back. Until that is sorted out, the loadout cannot be applied.

## What This Means
The files are not missing. Either something outside the app changed them after {Store} installed them, or the app's file data for this build of {Game} is wrong about them.

## Technical Details
Each file below exists in the game folder with a hash that differs from the one the app holds for {Game} v{Version} from {Store}.

The files with issues:

{Files}

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<Size>("Size")
            .AddValue<int>("FileCount")
            .AddValue<string>("Files")
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("Version")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameMoved = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 5))
        .WithTitle("Game moved to another folder")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Game} moved from {OldPath} to {NewPath}")
        .WithDetails("""
{Game} was in `{OldPath}` and is now in `{NewPath}`, so the loadouts on it follow the game to its new folder.

Files this app deployed with hardlinks or reflinks before a move to another drive are plain copies now. They are linked again as they are next written.

Dismiss this note once you have read it.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("OldPath")
            .AddValue<string>("NewPath")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate NonVanillaGameFiles = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Game files differ from the store's build")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{FileCount} file(s) in the {Game} folder are not the files {Store} installed")
        .WithDetails("""
{FileCount} file(s) in the {Game} folder do not match the build {Store} installed. They are most likely mods or edits that were already there before this app managed the game. If the loadout is deployed with links, these are the files that get backed up before deployment.

{Files}

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<int>("FileCount")
            .AddValue<string>("Files")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameBuildChanged = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 6))
        .WithTitle("Game updated since mods were applied")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Game} has changed since this loadout was last applied")
        .WithDetails("""
{Store} changed the files of {Game} after this loadout was last applied. {WhatBreaks}

Mods installed outside the app are not listed here. If a mod stops working, check its page for a release matching the game's version.

Applying the loadout again clears this note.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("WhatBreaks")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameBuildChangedWithMods = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("Game updated since mods were applied")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Game} has changed since this loadout was last applied. Installed mods that hook the game's code ({ModCount}) may be broken")
        .WithDetails("""
{Store} changed the files of {Game} after this loadout was last applied. {WhatBreaks}

These installed mods hook the game's code:

{Mods}

Mods installed outside the app are not listed here. If a mod stops working, check its page for a release matching the game's version.

Applying the loadout again clears this note.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("WhatBreaks")
            .AddValue<int>("ModCount")
            .AddValue<string>("Mods")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameRecentlyUpdated = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("Game updated recently")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Store} updated this install of {Game} on {Date}. Mods that hook the game's code may need updates")
        .WithDetails("""
{Store} finished writing this update on {Date}. {WhatBreaks}

These installed mods hook the game's code:

{Mods}

Mods installed outside the app are not listed here. If a mod stops working, check its page for a release matching the game's version.

{UpdateAdvice}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("Date")
            .AddValue<string>("WhatBreaks")
            .AddValue<string>("Mods")
            .AddValue<string>("UpdateAdvice")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameUpdateWaiting = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 9))
        .WithTitle("Update waiting in Steam")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("{Store} has an update for {Game} that hasn't installed yet. Once it does, mods that hook the game's code may stop working until they're updated")
        .WithDetails("""
{WhenItInstalls}

The app won't rebuild its file index while the update is pending.

This note clears on its own once the update is installed.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Store")
            .AddValue<string>("WhenItInstalls")
        )
        .Finish();
}
