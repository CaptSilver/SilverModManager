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
/// over one set of helpers, not classes.
/// </summary>
/// <remarks>
/// The switches (<c>-D:</c>, <c>-I:</c>, <c>-P:</c>, <c>-O:</c> and the game modes) are the ones xEdit's
/// <c>wbInit.pas</c> reads. Which output each tool's second run must not see, and which of its files are logs and
/// caches, is not settled, so no definition sets <c>DisableOwnOutputDuringRun</c> or <c>IgnoredOutput</c>.
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

    private static readonly string[] GameFolderSwitches = ["-D:{DataFolder}", "-I:{IniFile}", "-P:{PluginsFile}"];

    public static IReadOnlyList<ToolDefinition> All { get; } = Build();

    public static IServiceCollection AddCreationEngineTools(this IServiceCollection services)
    {
        foreach (var definition in All) services.AddToolDefinition(definition);
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
