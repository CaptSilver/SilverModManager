using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Generators.Diagnostics;

namespace SilverModManager.Games.Generic.Diagnostics;

internal static partial class Diagnostics
{
    private const string Source = "SilverModManager.Games.Generic";

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

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredWineDllOverrides = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 6))
        .WithTitle("Incorrect WINE configuration")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Update the WINE DLL overrides to mod {GameName} on Linux")
        .WithDetails("""
Modding {GameName} on Linux requires you to configure WINE or its mods will not load.

A WINE DLL override tells WINE to load a DLL file that sits next to the game instead of its own built-in version of that DLL.

### How to Resolve
{Instructions}

### Technical Details
Many mod loaders hook into the game by replacing a system DLL. On Windows the game loads that DLL from its own folder. WINE prefers its built-in copy unless an override says otherwise, so the loader never starts.

The WINE DLL overrides of this game's prefix do not include the ones its mod loader needs.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Instructions")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredPrefixComponents = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 7))
        .WithTitle("Missing prefix components")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Install the components {GameName} needs in its Wine prefix")
        .WithDetails("""
Modding {GameName} on Linux requires components that its Wine prefix does not have yet:

{Missing}
{Unverified}
### How to Resolve
Use "Set up prefix" to install them with the app's own installer, or do it by hand:

{Instructions}

### Technical Details
The prefix was read from disk without running anything. A DLL in `system32` does not count as installed on its own, because Wine puts placeholder DLLs there; the check needs the real files or the component's registry entry.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Missing")
            .AddValue<string>("Unverified")
            .AddValue<string>("Instructions")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate LoadoutItemsTargetGameWrittenPath = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 8))
        .WithTitle("Files Target A File The Game Writes Itself")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("'{Group}' has files the game writes itself")
        .WithDetails("""
**{Group}** contains files at paths that {GameName} writes itself, so these files are left out when the loadout is
applied:

{Paths}

The files stay in the loadout. The game rebuilds them when it starts, and a copy from a mod would be overwritten
or would stop the game from refreshing its own.

Remove the files from the mod if they are not meant to be there.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddDataReference<LoadoutItemGroupReference>("Group")
            .AddValue<string>("GameName")
            .AddValue<string>("Paths")
        )
        .Finish();
    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OldVisualCppRuntime = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 9))
        .WithTitle("Old Visual C++ runtime in the prefix")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{GameName}'s prefix has an old Visual C++ runtime that its mods can crash on")
        .WithDetails("""
Mods in the loadout deploy files that need the Visual C++ 2015-2022 runtime (`msvcp140.dll` or `vcruntime140.dll`):

{Files}

The prefix's `msvcp140.dll` is {Found}, and these mods need version 14.40 or newer. With an older runtime a mod's DLL
can crash the game while it loads, before any error shows.

### How to Resolve
Use "Set up prefix" to install the runtime with the app's own installer, or do it by hand:

{Instructions}

### Technical Details
The check reads the prefix's `drive_c/windows/system32/msvcp140.dll` file version and the import tables of the DLLs and
executables the loadout provides. Nothing is run.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("Files")
            .AddValue<string>("Found")
            .AddValue<string>("Instructions")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate OpenXrRuntimeNotRegistered = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 10))
        .WithTitle("No OpenXR runtime registered")
        .WithSeverity(DiagnosticSeverity.Warning)
        .WithSummary("{GameName} uses OpenComposite, but no OpenXR runtime is registered")
        .WithDetails("""
The loadout replaces `openvr_api.dll` with OpenComposite, which runs the game through OpenXR instead of OpenVR. OpenXR
finds the headset's software through `{ConfigFile}`, and {Problem}.

Without a runtime the game starts, finds no headset and can crash or show nothing in VR.

### How to Resolve
Make SteamVR the OpenXR runtime, either way works:

* In SteamVR open Settings, turn on "Advanced settings", go to Developer and press "Set SteamVR as OpenXR Runtime".
* Or link the file by hand to SteamVR's manifest:

```
mkdir -p ~/.config/openxr/1
ln -sf <SteamVR folder>/steamxr_linux64.json ~/.config/openxr/1/active_runtime.json
```

Another runtime, such as Monado or WiVRn, works the same way once its manifest is linked there.

### Technical Details
OpenXR reads `$XDG_CONFIG_HOME/openxr/1/active_runtime.json` (`~/.config` when the variable is unset) and loads the library
its `runtime.library_path` names.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("GameName")
            .AddValue<string>("ConfigFile")
            .AddValue<string>("Problem")
        )
        .Finish();
}
