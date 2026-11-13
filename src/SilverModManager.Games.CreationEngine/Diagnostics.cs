using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.CreationEngine;

internal static partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.CreationEngine";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingMaster = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Master")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{PluginName} requires {MissingMasterPluginName} which is not installed")
        .WithDetails("""
The mod **{PluginName}** requires **{MissingMasterPluginName}** to function, but **{MissingMasterPluginName}** is not provided by any mods.

This means the plugin can’t find one of its required “master files.” Masters contain important data that other mods depend on—without them, the plugin is left pointing to records that don’t exist.

If you try to launch the game in this state, it won’t start. Bethesda games stop loading when a master is missing to prevent crashes, broken saves, or corrupted load orders. 
To fix this, you’ll need to install the missing master before playing.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Mod")
            .AddValue<string>("PluginName")
            .AddValue<string>("MissingMasterPluginName")
        )
        .Finish();

    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingMasterIsDisabled = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Master")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("{PluginName} requires {MissingMasterPluginName} which is installed in {MissingMasterMod} but is disabled")
        .WithDetails("""
The mod **{PluginName}** requires **{MissingMasterPluginName}** to function, but it is not provided by any enabled mods. The 
mod **{MissingMasterMod}** provides this file, but it is disabled. Enable the mod to fix this issue.

This means the plugin can’t find one of its required “master files.” Masters contain important data that other mods depend on—without them, the plugin is left pointing to records that don’t exist.

If you try to launch the game in this state, it won’t start. Bethesda games stop loading when a master is missing to prevent crashes, broken saves, or corrupted load orders. 
To fix this, you’ll need to install the missing master before playing.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Mod")
            .AddValue<string>("PluginName")
            .AddValue<string>("MissingMasterPluginName")
            .AddValue<LoadoutItemGroupReference>("MissingMasterMod")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate UnreadablePlugins = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("Unreadable Plugins")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The headers of {PluginCount} plugin(s) could not be read: {PluginNames}")
        .WithDetails("""
The app could not read the plugin file(s) below, so it cannot check whether their masters are present or put them in a load order:

{PluginNames}

Neither the app's backups nor the game folder hold a readable copy of them. For a game file this usually means it is missing or damaged in the game folder.

Verify the game files in the game store, then reopen the app.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<int>("PluginCount")
            .AddValue<string>("PluginNames")
        )
        .Finish();

    // Number 4 was the "two OpenMW configs" warning; it is gone and the number is not reused.
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate TestFileEntries = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 5))
        .WithTitle("Game ini sets test files")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{IniKeys} set in the game's ini, so the game ignores its plugin list")
        .WithDetails("""
The game's ini sets **{IniKeys}**. When a test file is set, the game stops reading Plugins.txt (and the Creation Club
list, where it has one) and loads the plugins named there instead, so the load order shown in the app does not decide what loads.

The app does not model this. Remove the entries from the ini to use the load order, or leave them if you set them
on purpose.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("IniKeys")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate PluginListReplaced = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 6))
        .WithTitle("Game's plugin list changes were replaced")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("At {ReplacedAt} the app replaced changes made to the game's plugin list: {Summary}")
        .WithDetails("""
The plugin list the game keeps on disk was changed outside the app, and the last time the loadout was applied the app wrote its own list over it:

{Summary}

The app owns the plugin list. Switching a plugin on or off and changing the load order are done on the Load Order page; edits made to the file by the game or by another tool are not kept.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("ReplacedAt")
            .AddValue<string>("Summary")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameInisTakenOver = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("Loadout will manage the game's ini files")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("The next apply gives this loadout its own copy of {Files}")
        .WithDetails("""
The next time this loadout is applied, it takes its own copy of the game's ini files: **{Files}**.

The files on disk now become the loadout's starting point, so the settings you have today are kept. From then on the
app writes each file from that copy, plus the ini tweaks of the enabled mods and the keys the app manages itself.
Switching to another loadout puts that loadout's copy in place.

Turn off "Own the game INIs" for the loadout to leave these files to the game.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Files")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameIniChangesReplaced = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("Game's ini changes will be replaced")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The game changed settings in {File} that the app sets itself")
        .WithDetails("""
Since the loadout was last applied, the game changed settings in **{File}** that the app does not leave to the game:

{Settings}

The next apply writes the app's value over each of them. Settings in the file that nothing else sets are kept: the
loadout adopts them into its own copy of the file.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("File")
            .AddValue<MarkdownFragment>("Settings")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RepeatedCrashNamingAMod = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 10))
        .WithTitle("Repeated crash")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Count} crashes with one cause: {Cause}")
        .WithDetails("""
The game crashed {Count} times in the same place. The crash logger's reports point at one cause: {Cause}.

Look at the mod **{Mod}** first. {Fix}.

The files these crashes touched, and the mods that supply them:

{Owners}

Most recent crash: {LastSeen}. Read against {Origin}.

Crashes appear here when Health Check is opened or refreshed after the game exits.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Mod")
            .AddValue<int>("Count")
            .AddValue<string>("Where")
            .AddValue<string>("Cause")
            .AddValue<string>("Fix")
            .AddValue<MarkdownFragment>("Owners")
            .AddValue<string>("Origin")
            .AddValue<string>("LastSeen")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RepeatedCrash = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 11))
        .WithTitle("Repeated crash")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Count} crashes while {Where}")
        .WithDetails("""
The game crashed {Count} times in the same place. Cause: {Cause}.

{Fix}.

Mods whose files the crash touched:

{Owners}

Most recent crash: {LastSeen}. Read against {Origin}.

Crashes appear here when Health Check is opened or refreshed after the game exits.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<int>("Count")
            .AddValue<string>("Where")
            .AddValue<string>("Cause")
            .AddValue<string>("Fix")
            .AddValue<MarkdownFragment>("Owners")
            .AddValue<string>("Origin")
            .AddValue<string>("LastSeen")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OneOffCrash = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 12))
        .WithTitle("Crash seen once")
        .WithSeverity(DiagnosticSeverity.Suggestion)
        .WithSummary("One crash while {Where}")
        .WithDetails("""
The game crashed once. Cause: {Cause}.

{Fix}.

Seen once; the mods whose files it touched are listed so a repeat is easy to spot:

{Owners}

Crash time: {LastSeen}. Read against {Origin}.

Crashes appear here when Health Check is opened or refreshed after the game exits.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Where")
            .AddValue<string>("Cause")
            .AddValue<string>("Fix")
            .AddValue<MarkdownFragment>("Owners")
            .AddValue<string>("Origin")
            .AddValue<string>("LastSeen")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate StartupCrash = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 13))
        .WithTitle("The game crashed while starting")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("The game crashed while starting: {Cause}")
        .WithDetails("""
The game crashed before it finished starting, {Count} time(s) so far. {Cause}.

{Fix}.

The files the crash touched, and the mods that supply them:

{Owners}

Most recent crash: {LastSeen}. Read against {Origin}.

Crashes appear here when Health Check is opened or refreshed after the game exits.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<int>("Count")
            .AddValue<string>("Cause")
            .AddValue<string>("Fix")
            .AddValue<MarkdownFragment>("Owners")
            .AddValue<string>("Origin")
            .AddValue<string>("LastSeen")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OpenMwDanglingContent = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 14))
        .WithTitle("OpenMW's config lists plugins that are not there")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("openmw.cfg lists {Names} but no data directory has them")
        .WithDetails("""
OpenMW will not start until these lines are removed. Remove them in the OpenMW launcher's Content Files tab, or delete the lines from {Path}.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Names")
            .AddValue<string>("Path")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OpenMwGroundcoverOff = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 15))
        .WithTitle("OpenMW's groundcover is switched off")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Groundcover is off in OpenMW's settings.cfg; the groundcover plugins will not load. Turn it on in the OpenMW launcher")
        .WithDetails("""
These plugins are marked as groundcover in the load order, so they are written as `groundcover=` lines: {Plugins}. OpenMW skips every one of them while the Groundcover setting in {Path} is off. Turn on "Enable separate groundcover handling" in the OpenMW launcher's Advanced tab.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Plugins")
            .AddValue<string>("Path")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OpenMwUnregisteredArchive = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 16))
        .WithTitle("OpenMW does not load these archives")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Names} are deployed but openmw.cfg has no fallback-archive= line for them, so OpenMW loads nothing from them")
        .WithDetails("""
OpenMW only reads the archives its config names. Add a `fallback-archive=` line for each one to openmw.cfg, or register them in the OpenMW launcher's Data Files tab.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Names")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MorrowindOpenMwRetired = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 17))
        .WithTitle("OpenMW has its own entry now")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("OpenMW is now its own game. This loadout no longer writes openmw.cfg; manage OpenMW from its own entry in My Games. openmw.cfg still lists these plugins from this loadout: {Plugins}. Removing them from this loadout will leave those lines pointing at nothing.")
        .WithDetails("""
An earlier version wrote this loadout's plugin order into openmw.cfg when it found one. The Morrowind entry now writes Morrowind.ini only and never changes openmw.cfg.

Add the OpenMW entry in My Games to manage OpenMW's plugin list and mods.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Plugins")
        )
        .Finish();
}
