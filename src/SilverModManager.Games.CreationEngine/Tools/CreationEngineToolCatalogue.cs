using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tools;

/// <summary>
/// The xEdit family: xEdit itself and the three LOD tools built on it. They take the same Data, ini and plugin list
/// switches, run from the same Visual C++ runtime and want the same Wine compatibility setting, so they are rows
/// over one set of helpers, not classes. The OpenComposite Unleashed configurator is a plain row beside them.
/// The behaviour and body tools (Pandora, Nemesis, BodySlide, Outfit Studio) are rows too.
/// </summary>
/// <remarks>
/// The switches (<c>-D:</c>, <c>-I:</c>, <c>-P:</c>, <c>-O:</c> and the game modes) are the ones xEdit's
/// <c>wbInit.pas</c> reads. Which output each xEdit-family tool's second run must not see, and which of its files are
/// logs and caches, is not settled, so those definitions set neither <c>DisableOwnOutputDuringRun</c> nor
/// <c>IgnoredOutput</c>. Pandora, Nemesis, BodySlide and Outfit Studio list their config, cache and log files from
/// their own source, because without that a run's settings file would be moved into the tool's group as mod output.
/// </remarks>
public static class CreationEngineToolCatalogue
{
    private static readonly GamePath StandardData = new(LocationId.Game, "Data");

    private static readonly (GameId Game, string Prefix)[] EditorNames =
    [
        (SkyrimSE.SkyrimSE.GameId, "SSEEdit"),
        (SkyrimLE.SkyrimLE.GameId, "TES5Edit"),
        (SkyrimVR.SkyrimVR.GameId, "TES5VREdit"),
        (Fallout4.Fallout4.GameId, "FO4Edit"),
        (Fallout4VR.Fallout4VR.GameId, "FO4VREdit"),
        (Fallout3.Fallout3.GameId, "FO3Edit"),
        (FalloutNewVegas.FalloutNewVegas.GameId, "FNVEdit"),
        (Oblivion.Oblivion.GameId, "TES4Edit"),
    ];

    private static readonly string[] GenericEditorNames = ["xEdit.exe", "xEdit64.exe"];

    private static readonly GameId[] LodGames =
    [
        SkyrimSE.SkyrimSE.GameId,
        SkyrimLE.SkyrimLE.GameId,
        SkyrimVR.SkyrimVR.GameId,
        EnderalSE.EnderalSE.GameId,
        Fallout4.Fallout4.GameId,
    ];

    private static readonly string[] XLodGenNames = ["xLODGen.exe", "xLODGenx64.exe"];
    private static readonly string[] TexGenNames = ["TexGen.exe", "TexGenx64.exe"];
    private static readonly string[] DynDolodNames = ["DynDOLOD.exe", "DynDOLODx64.exe"];

    // Ships in the Data folder of OpenComposite Unleashed's Nexus archive; a self-contained .NET 9 program.
    private static readonly string[] OcUnleashedConfiguratorNames = ["OC Unleashed Configurator for Skyrim VR.exe"];

    // Pandora's Nexus archive ships one exe, a .NET 8 WPF app. Nemesis is a native Qt program built against the
    // shared Visual C++ runtime. BodySlide's archive holds BodySlide and Outfit Studio side by side in
    // Data/CalienteTools/BodySlide; they are separate entries so a scan of that folder saves each under its own name.
    private static readonly string[] PandoraNames = ["Pandora Behaviour Engine+.exe"];
    private static readonly string[] NemesisNames = ["Nemesis Unlimited Behavior Engine.exe"];
    private static readonly string[] BodySlideNames = ["BodySlide x64.exe", "BodySlide.exe"];
    private static readonly string[] OutfitStudioNames = ["Outfit Studio x64.exe"];

    private static readonly GameId[] PandoraGames = [SkyrimSE.SkyrimSE.GameId, SkyrimVR.SkyrimVR.GameId];
    private static readonly GameId[] NemesisGames = [SkyrimSE.SkyrimSE.GameId, SkyrimLE.SkyrimLE.GameId, SkyrimVR.SkyrimVR.GameId];

    private static readonly GameId[] BodySlideGames =
    [
        SkyrimSE.SkyrimSE.GameId,
        SkyrimLE.SkyrimLE.GameId,
        SkyrimVR.SkyrimVR.GameId,
        Fallout4.Fallout4.GameId,
        Fallout4VR.Fallout4VR.GameId,
    ];

    // These are matched against the path inside the game folder, so they start at Data.
    // Pandora writes into its output folder, which is Data unless its own Settings.json names another, and keeps
    // Settings.json beside its exe, which the archive puts at the root of Data. It parses --output but neither
    // 4.4.0-beta nor 5.0.0-beta reads the value, so it cannot be pointed at a run folder like the LOD tools.
    // Pandora_Engine is its working set: the active mod list, the list of files it wrote last time, and Nemesis
    // patches it converted for itself. Its shipped files there are not in the delta because the run does not change them.
    private static readonly string[] PandoraIgnored =
    [
        "Data/Pandora_Engine/**",
        "Data/Engine.log",
        "Data/Pandora_CriticalCrash_*.log",
        "Data/Settings.json",
    ];

    // Nemesis insists on running from Data/Nemesis_Engine and writes its cache, logs and ini relative to that.
    private static readonly string[] NemesisIgnored =
    [
        "Data/Nemesis_Engine/cache/**",
        "Data/Nemesis_Engine/temp_behaviors/**",
        "Data/Nemesis_Engine/nemesis.ini",
        "Data/Nemesis_Engine/CriticalLog.txt",
        "Data/Nemesis_Engine/UpdateLog.txt",
        "Data/Nemesis_Engine/PatchLog.txt",
    ];

    // BodySlide and Outfit Studio save their settings and logs beside their exe in CalienteTools/BodySlide.
    private static readonly string[] BodySlideIgnored =
    [
        "Data/CalienteTools/BodySlide/Config.xml",
        "Data/CalienteTools/BodySlide/BodySlide.xml",
        "Data/CalienteTools/BodySlide/OutfitStudio.xml",
        "Data/CalienteTools/BodySlide/AnimationFavorites.xml",
        "Data/CalienteTools/BodySlide/Log_BS.txt",
        "Data/CalienteTools/BodySlide/Log_OS.txt",
    ];

    private static readonly string[] GameFolderSwitches = ["-D:{DataFolder}", "-I:{IniFile}", "-P:{PluginsFile}"];

    // Declared before All: static fields initialise in order, and Build reads it.
    private static readonly ToolPrefixRequirements VisualCppOnly = new() { Components = [PrefixComponents.VisualCppRuntime.Id] };

    public static IReadOnlyList<ToolDefinition> All { get; } = Build();

    /// <summary>Terrain LOD, then textures, then objects: each step reads what the one before wrote.</summary>
    public static ToolPipeline LodGeneration { get; } = new("LOD generation", [new ToolId("xlodgen"), new ToolId("texgen"), new ToolId("dyndolod")]);

    public static IServiceCollection AddCreationEngineTools(this IServiceCollection services)
    {
        foreach (var definition in All) services.AddToolDefinition(definition);
        services.AddToolPipeline(LodGeneration);
        return services;
    }

    private static IReadOnlyList<ToolDefinition> Build()
    {
        var editorExeNames = EditorNames.SelectMany(n => EditorFileNames(n.Prefix)).Concat(GenericEditorNames).ToArray();

        var xedit = new ToolDefinition
        {
            Id = new ToolId("xedit"),
            DisplayName = "xEdit",
            AppliesTo = HasDataAndIni,
            ExecutableNames = game => [.. OwnEditorNames(game), .. GenericEditorNames],
            Arguments = new ArgumentTemplate(GameFolderSwitches),
            ExtraArguments = (game, exe) => XEditGameMode.Arguments(game, exe),
            Prefix = XEditPrefix(editorExeNames),
            Output = new ToolOutput.GameFolder("xEdit Output"),
            ExitCodes = ExitCodePolicy.AnyExitIsFine,
        };
        xedit = xedit with
        {
            QuickAutoClean = new ToolVariant(
                "Quick Auto Clean",
                new ArgumentTemplate([.. GameFolderSwitches, "-quickautoclean", "-autoload", "-autoexit", "-IKnowWhatImDoing", "{Plugin}"]),
                ExitCodePolicy.ZeroIsSuccess),
        };

        return
        [
            xedit,
            LodTool("xlodgen", "xLODGen", XLodGenNames, HasStandardData, extraSwitches: ["-autoexit"]),
            LodTool("texgen", "TexGen", TexGenNames, game => LodGames.Contains(game.GameId)),
            LodTool("dyndolod", "DynDOLOD", DynDolodNames, game => LodGames.Contains(game.GameId)),
            PatcherTool("pandora", "Pandora Behaviour Engine+", "Pandora Output", PandoraNames, PandoraGames, new ToolPrefixRequirements
            {
                Components = [PrefixComponents.DotNetDesktop8.Id],
                RegistryDefaults = [new PrefixRegistryDefault(PandoraNames[0], "Decorated", "N")],
            }, PandoraIgnored),
            PatcherTool("nemesis", "Nemesis Unlimited Behavior Engine", "Nemesis Output", NemesisNames, NemesisGames, VisualCppOnly, NemesisIgnored),
            PatcherTool("bodyslide", "BodySlide", "BodySlide Output", BodySlideNames, BodySlideGames, VisualCppOnly, BodySlideIgnored),
            PatcherTool("outfit-studio", "Outfit Studio", "Outfit Studio Output", OutfitStudioNames, BodySlideGames, VisualCppOnly, BodySlideIgnored),
            new ToolDefinition
            {
                Id = new ToolId("oc-unleashed-configurator"),
                DisplayName = "OpenComposite Unleashed Configurator",
                AppliesTo = game => game.GameId == SkyrimVR.SkyrimVR.GameId,
                ExecutableNames = _ => OcUnleashedConfiguratorNames,
                Arguments = ArgumentTemplate.Empty,
                // It edits opencomposite.ini and ConfiguratorUI.json beside the game; without a group those writes
                // sit in Overrides where nothing says where they came from.
                Output = new ToolOutput.GameFolder("OpenComposite Unleashed Configurator output"),
                IgnoredOutput = ["**/*.log", "**/*.log?"],
                ExitCodes = ExitCodePolicy.AnyExitIsFine,
            },
        ];
    }

    private static ToolDefinition LodTool(string id, string name, string[] exeNames, Func<IGame, bool> appliesTo, string[]? extraSwitches = null) => new()
    {
        Id = new ToolId(id),
        DisplayName = name,
        AppliesTo = appliesTo,
        ExecutableNames = _ => exeNames,
        Arguments = new ArgumentTemplate([.. GameFolderSwitches, "-O:{OutputFolder}", .. extraSwitches ?? []]),
        ExtraArguments = (game, exe) => XEditGameMode.Arguments(game, exe),
        Prefix = XEditPrefix(exeNames),
        Output = new ToolOutput.DeclaredFolder($"{name} Output", StandardData),
    };

    // Tools that read and write the game's Data folder on their own, from their own settings, so they take no
    // switches; what they write is picked up from the game folder after the run.
    private static ToolDefinition PatcherTool(string id, string name, string outputGroup, string[] exeNames, GameId[] games, ToolPrefixRequirements prefix, string[] ignoredOutput) => new()
    {
        Id = new ToolId(id),
        DisplayName = name,
        AppliesTo = game => games.Contains(game.GameId),
        ExecutableNames = _ => exeNames,
        Arguments = ArgumentTemplate.Empty,
        Prefix = prefix,
        Output = new ToolOutput.GameFolder(outputGroup),
        IgnoredOutput = ignoredOutput,
        ExitCodes = ExitCodePolicy.AnyExitIsFine,
    };

    private static ToolPrefixRequirements XEditPrefix(IEnumerable<string> exeNames) => new()
    {
        Components = [PrefixComponents.VisualCppRuntime.Id],
        RegistryDefaults = exeNames.Select(exe => new PrefixRegistryDefault(exe, "Version", "winxp")).ToArray(),
    };

    private static IEnumerable<string> EditorFileNames(string prefix) => [$"{prefix}.exe", $"{prefix}64.exe"];

    private static IEnumerable<string> OwnEditorNames(IGame game) => EditorNames
        .Where(n => n.Game == game.GameId)
        .SelectMany(n => EditorFileNames(n.Prefix));

    // A game without a main ini has nothing to give -I:, and the argument resolver would refuse the run by name.
    private static bool HasDataAndIni(IGame game) =>
        game is ICreationEngineGame && game is IGameIniFiles { ManagedKeysFile: not null };

    private static bool HasStandardData(IGame game) => game is ICreationEngineGame { DataPath: var data } && data == StandardData;
}
