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

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OpenMwConfigAmbiguous = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("Two OpenMW configs found")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("Found an openmw.cfg in both {NativePath} and {FlatpakPath}")
        .WithDetails("""
Both of these folders hold an openmw.cfg:

{NativePath}

{FlatpakPath}

The app writes the mod load order into the config OpenMW reads, and cannot tell which of the two you use, so it will not write to either.

Remove or rename the openmw.cfg of the OpenMW you do not use, then reopen the app.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("NativePath")
            .AddValue<string>("FlatpakPath")
        )
        .Finish();

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
}
