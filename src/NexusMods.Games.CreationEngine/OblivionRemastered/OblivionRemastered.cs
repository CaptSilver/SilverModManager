using DynamicData.Kernel;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Installers;
using NexusMods.Games.FOMOD;
using NexusMods.Games.Unreal;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.OblivionRemastered;

/// <summary>
/// The Unreal Engine 5 remake of Oblivion. It still loads Bethesda plugins, from a Data folder buried in the
/// project's Content, and takes Unreal paks and UE4SS mods besides. The game ships its own Data/Plugins.txt and
/// the app does not write it. No plugin header reader exists for it, so no masters are checked.
/// </summary>
/// <remarks>
/// Sources for every path below are Mod Organizer 2's basic_games plugin
/// (https://github.com/ModOrganizer2/modorganizer-basic_games/blob/master/games/game_oblivion_remaster.py)
/// and its script extender helper (games/oblivion_remaster/script_extender.py).
/// </remarks>
public class OblivionRemastered(IServiceProvider provider) : ABethesdaGame<OblivionRemastered>(provider), IGameData<OblivionRemastered>
{
    private const string ProjectFolder = "OblivionRemastered";

    public static GameId GameId { get; } = GameId.From("CreationEngine.OblivionRemastered");
    public static string DisplayName => "Oblivion Remastered";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(7587);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // appmanifest of the Steam release. No GOG release exists.
        SteamAppIds = [2623190u],
    };

    // The root launcher is what Steam starts; the engine binary sits in the project's Binaries/Win64.
    protected override RelativePath PrimaryExe => "OblivionRemastered.exe";

    private const string EngineExe = $"{ProjectFolder}/Binaries/Win64/OblivionRemastered-Win64-Shipping.exe";

    // game_oblivion_remaster.py, GameDataPath.
    public override GamePath DataPath => new(LocationId.Game, $"{ProjectFolder}/Content/Dev/ObvData/Data");

    // script_extender.py: obse64_loader.exe is installed in OblivionRemastered\Binaries\Win64, with its
    // plugins under OBSE/Plugins beside it.
    protected override GamePath EnginePath => new(LocationId.Game, $"{ProjectFolder}/Binaries/Win64");

    protected override string[] EngineFiles => [@"obse64_loader\.exe", @"obse64_.*\.dll"];
    protected override string[] GameAliases => ["Oblivion Remastered", "OblivionRemastered", "TES4R"];

    // game_oblivion_remaster.py: the game ignores the Documents redirect and always keeps its saves in
    // UserHome\Documents\My Games\Oblivion Remastered\Saved\SaveGames. Nothing fetched names a LocalAppData
    // folder for this game, so no AppData location is registered.
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Oblivion Remastered";

    public override IReadOnlyList<GamePath> ProtectedFolders => [new GamePath(LocationId.Preferences, "Saved/SaveGames")];

    /// <summary>Paks, LogicMods and UE4SS under the Unreal project.</summary>
    protected UnrealModLayout Unreal { get; } = UnrealModLayout.ForProject(new GamePath(LocationId.Game, ProjectFolder));

    protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider) =>
    [
        FomodXmlInstaller.Create(provider, DataPath),
        new OblivionRemasteredInstaller(provider)
        {
            ProjectFolder = ProjectFolder,
            Layout = Unreal,
            DataPath = DataPath,
            DataFolderName = DataFolderName,
            EnginePath = EnginePath,
            GameAliases = GameAliases,
            TopLevelDirs = TopLevelDirs,
            EngineFiles = EngineFiles,
            PluginLikeExtensions = PluginLikeExtensions,
            ArchiveLikeExtensions = ArchiveLikeExtensions,
        },
    ];

    // The version lives in the engine binary; the launcher at the root carries none of the game's.
    public override Optional<Version> GetLocalVersion(GameInstallation installation)
    {
        try
        {
            var exe = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, EngineExe));
            return exe.FileInfo.GetFileVersionInfo().ProductVersion;
        }
        catch (Exception)
        {
            return Optional<Version>.None;
        }
    }
}
