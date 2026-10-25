using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.FromSoft;

internal static partial class FromSoftDiagnostics
{
    internal const string Source = "SilverModManager.Games.FromSoft";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate PackageCollision = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Mods provide the same file")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Packages} provide {Path}: {Reason}")
        .WithDetails("""
More than one mod provides the file **{Path}**: {Packages}.

The game loads one of them and ignores the rest, and which one wins is not something the app can promise. The app
does not merge this file: {Reason}. Disable or remove all but one of them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Packages")
            .AddValue<string>("Path")
            .AddValue<string>("CaseNote")
            .AddValue<string>("Reason")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OverlayCollision = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Mods provide the same configuration file")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Mods} provide {Path} in the game's configuration layer")
        .WithDetails("""
More than one mod puts a file at **{Path}** in the layer that is placed over the game folder for a session: {Mods}.

Only the file that is written last ends up there, and the others are lost without any message. Disable or remove
all but one of them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Mods")
            .AddValue<string>("Path")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate Me3HostMissing = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("me3 is not installed")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("me3 is not installed for this loadout")
        .WithDetails("""
This loadout has mods, and mods are loaded into {Game} by me3, but the loadout has no me3 files.

The app installs me3 automatically when the first mod arrives: it downloads the Linux release
(**{Release}** from the me3 releases on GitHub), checks it against the digest GitHub publishes and installs it into this
loadout. That has not happened here, usually because the download failed. Install me3 retries it. The Windows zip does
not work here.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Release")
            .AddValue<string>("Game")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OverlayFileLeftBehind = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("A session left a file in the game's overlay")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Path} was left in the overlay of the game folder")
        .WithDetails("""
The game wrote **{Path}** into the overlay that is placed over its folder for a modded session, and the app does not
know it as a runtime file. {Reason}

The real game folder is not changed. Discard the file to start the next modded session; the app refuses to start one
while it is there.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Path")
            .AddValue<string>("Reason")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameFolderChangedInSession = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 5))
        .WithTitle("The game folder changed during a modded session")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Path} in the game folder was {Change} during a modded session")
        .WithDetails("""
**{Path}** in the real game folder was {Change} while the game ran with mods. A modded session is meant to write
only into its overlay, so the overlay did not hold and the game wrote to its real folder.

Verify the game's files in Steam to put the folder back, and do not start another modded session until the cause is
known. Discarding this only records that you have looked at it.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Path")
            .AddValue<string>("Change")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameFolderArtifacts = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 6))
        .WithTitle("The game folder holds mod files")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("The {Game} game folder is not in its vanilla state")
        .WithDetails("""
{Finding}

Mods are loaded from their own folder, and a modded session runs on an overlay, so the app does not put files in the
game folder. These come from an earlier mod manager or a manual install. While the game's anti-cheat launcher is
there, starting the game online from Steam can load them.

**Fix** lists what it would restore and remove, and changes the folder only after you confirm. If the earlier manager
marked the launcher immutable, the app asks for your password once to clear that one attribute with `chattr -i`.
Nothing else needs root. You can also verify the game's files in Steam to restore them.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Finding")
            .AddValue<string>("Game")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate ItemSpawnerPresent = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("An item-spawner mod is in the game folder")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Finding}")
        .WithDetails("""
{Finding}

Items a spawner creates mark a save as modified. Keep that save out of vanilla online play.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Finding")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OverlaySupportMissing = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("Modded launches are not possible here")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("The sandbox a modded launch needs is not available")
        .WithDetails("""
{Finding}

A modded launch runs the game on an overlay of its folder inside a user namespace, so the game folder itself is never
written to. Without it the app refuses to start the game with mods.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Finding")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate SaveWrittenByModdedSession = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 9))
        .WithTitle("A modded session changed the vanilla save")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Save} was changed by a modded session")
        .WithDetails("""
The game ran with mods and **{Save}**, the save the unmodded game uses, was not the same afterwards.

{Outcome} The bytes the session wrote are in **{Backup}**.

A mod that ignores the modded profile's save file is the usual cause. Steam Cloud may already have uploaded the
changed file before it was put back; the saves page explains how to clear that.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Save")
            .AddValue<string>("Backup")
            .AddValue<string>("Outcome")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate BuildChanged = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 10))
        .WithTitle("The installed Elden Ring build changed")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The installed build is not the one first launched through the app ({Kind})")
        .WithDetails("""
{Game}'s build was recorded the first time it was launched through the app. What is installed now differs:
{Changes}.

An update by Steam moves both. A reinstall or a different depot build under the same game data is reported as
repackaged. Mods and settings made for the earlier build may not match this one, so check them before playing online.

Re-stamp once you have, to take the installed build as the new reference.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Kind")
            .AddValue<string>("Changes")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MergeNote = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 11))
        .WithTitle("A merge made a choice between mods")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Merging {Path}: {Note}")
        .WithDetails("""
**{Path}** is provided by {Mods}, so the app merges the copies when the loadout is applied.

{Note}

Nothing is refused and the apply goes ahead; the note says which copy the merged file keeps. If that is not the one
you want, disable the mod whose change was dropped.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Path")
            .AddValue<string>("Mods")
            .AddValue<string>("Note")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MergeRefused = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 12))
        .WithTitle("The mods can't be merged")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Message}")
        .WithDetails("""
{Message}

Applying the loadout stops before anything is written, because deploying the copies whole would leave it to the game
which one wins. Disable or remove one of the mods that provide the file.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Message")
            .AddValue<string>("Path")
            .AddValue<string>("Strategy")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate HealPlan = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 13))
        .WithTitle("The merged regulation needs rebuilding for the new build")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Game} was updated, so the mods' regulations have to be rebased onto the new build")
        .WithDetails("""
{Game} was updated and this loadout has mods that ship a regulation. They were built for an earlier build, so the app
merges them onto the new one when the loadout is applied:

{Steps}

`silvermod fromsoft heal` keeps the new regulation as the baseline and checks the mods' param layouts now; add
`--dry-run` to only print the steps. A mod whose layout no longer fits needs an update from its author.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Steps")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate HealRefused = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 14))
        .WithTitle("The game's regulation was changed without an update")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{Message}")
        .WithDetails("""
{Message}

Merging mods onto a regulation that something else rewrote would build every merged file on the wrong rows, so the
app does not take it as the vanilla one.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Message")
        )
        .Finish();
}
