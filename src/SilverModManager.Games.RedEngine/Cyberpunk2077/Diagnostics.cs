using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Diagnostics.Values;

using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;
using SilverModManager.Generators.Diagnostics;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077;

public static partial class Diagnostics
{
    public const string Source = "SilverModManager.Games.RedEngine.Cyberpunk2077";

    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate MissingModWithKnownNexusUri = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' which is not installed")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyName}** is not installed.


### How to Resolve
1. Download **{DependencyName}** from {NexusModsDependencyUri}
2. Add **{DependencyName}** to the loadout. 

OR 

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}` which indicates that the **{DependencyName}** mod is required.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("DependencyName")
            .AddValue<NamedLink>("NexusModsDependencyUri")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
        )
        .Finish();

    
    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate MissingModWithKnownNexusUriWithStringSegment = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Missing Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' which is not installed")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyName}** is not installed.


### How to Resolve
1. Download **{DependencyName}** from {NexusModsDependencyUri}
2. Add **{DependencyName}** to the loadout. 

OR 

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}`. The file has a reference to **{DependencyName}** on line {LineNumber} indicating that the **{DependencyName}** mod is required.

```
{MatchingSegment}
```
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("DependencyName")
            .AddValue<NamedLink>("NexusModsDependencyUri")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
            .AddValue<string>("MatchingSegment")
            .AddValue<int>("LineNumber")
        )
        .Finish();

    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate DisabledGroupDependency = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' but it is disabled")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyGroup}** is not enabled.


### How to Resolve
1. Enable **{DependencyGroup}** in "Installed Mods"

OR

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}` which indicates that the **{DependencyName}** mod is required.

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddDataReference<LoadoutItemGroupReference>("DependencyGroup")
            .AddValue<string>("DependencyName")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
        )
        .Finish();
    
    [DiagnosticTemplate] 
    [UsedImplicitly] 
    internal static IDiagnosticTemplate DisabledGroupDependencyWithStringSegment = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Disabled Mod Dependency")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' requires '{DependencyName}' but it is disabled")
        .WithDetails("""
The mod **{Group}** requires **{DependencyName}** to function, but **{DependencyGroup}** is not enabled.


### How to Resolve
1. Enable **{DependencyGroup}** in "Installed Mods"

OR

1. Remove **{Group}** from "Installed Mods"

### Technical Details
{Explanation}

**{Group}** includes the file `{Path}` in `{SearchPath}` (or one of the subfolders) and has the extension `{SearchExtension}`. The file has a reference to **{DependencyName}** on line {LineNumber} indicating that the **{DependencyName}** mod is required.

```
{MatchingSegment}
```
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddDataReference<LoadoutItemGroupReference>("DependencyGroup")
            .AddValue<string>("DependencyName")
            .AddValue<string>("Explanation")
            .AddValue<GamePath>("Path")
            .AddValue<GamePath>("SearchPath")
            .AddValue<Extension>("SearchExtension")
            .AddValue<string>("MatchingSegment")
            .AddValue<int>("LineNumber")
        )
        .Finish();
    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate NoWayToRunRedMod = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 3))
        .WithTitle("No way to run REDmod")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("REDmod is a Windows program and nothing on this system can run it for Cyberpunk 2077.")
        .WithDetails("""
The REDmod DLC is installed, but it is a Windows program and the app found no way to run it for this copy of Cyberpunk 2077. REDmods will not be deployed until it can.

### How to Resolve
1. Make sure the game is set up to run through Proton, umu or Wine, for example by launching it once from Steam or Heroic.
2. Restart the app.

OR

1. Remove any mods requiring REDmod from "Installed Mods".

### Technical Details
{Reason}
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Reason")
        )
        .Finish();
    
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate MissingRedModDependency = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 4))
        .WithTitle("REDmod DLC is not installed")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("The official REDmod DLC is required for {ModCount} mod(s) but is not installed.")
        .WithDetails("""
The official REDmod DLC is a mod loader required to use some mods for Cyberpunk 2077. The loadout contains {ModCount} mod(s) that require REDmod to work, but it is not installed.

### How to Resolve
1. Install the REDmod DLC - View in {RedmodLink} or the {GenericLink}.
2. Apply a loadout with REDmod installed or restart the app.

OR

1. Remove any mods requring REDmod from Installed Mods.
2. Remove any subfolders of `{RedModFolder}` from External Changes.

### Technical Details 
Each subfolder in the `{RedModFolder}` represents an installed mod. If any folders are present, REDmod is required. 

After finding {ModCount} mod(s), the REDmod DLC was checked for installation by verifying that `{RedModEXE}` exists.

""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<NamedLink>("RedmodLink")
            .AddValue<NamedLink>("GenericLink")
            .AddValue<int>("ModCount")
            .AddValue<string>("RedModFolder")
            .AddValue<string>("RedModEXE")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate GameUpdatedSinceExtenderRan = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("Game updated since its mod loaders last ran")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{Game} is now version {GameVersion}, but {Extender} last ran on {LoggedVersion}")
        .WithDetails("""
{Game} is now version {GameVersion}. The last time {Extender} ran, the game was {LoggedVersion}. {Extender} and the mods that hook the game through it probably need updates before you launch.

{Plugins}
Look for releases built for {GameVersion} on {Red4ExtReleases}, {CetReleases} and the pages of the mods that load through them. Plain `.archive` mods are usually unaffected.

This clears itself once the game has been started with a matching release.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Extender")
            .AddValue<string>("GameVersion")
            .AddValue<string>("LoggedVersion")
            .AddValue<string>("Plugins")
            .AddValue<NamedLink>("Red4ExtReleases")
            .AddValue<NamedLink>("CetReleases")
        )
        .Finish();
}
