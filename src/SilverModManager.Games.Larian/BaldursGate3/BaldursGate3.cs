using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.Modsettings;
using SilverModManager.Games.Larian.Engine.Utils.LsxXmlParsing;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.BaldursGate3;

// Lists IWineDllOverrideRequirements itself, though ALarianGame implements it, because the game is where the
// extender's override is declared.
public class BaldursGate3(IServiceProvider provider) : ALarianGame<BaldursGate3>(provider), IGameData<BaldursGate3>, IWineDllOverrideRequirements
{
    public static GameId GameId { get; } = GameId.From("Larian.BaldursGate3");
    public static string DisplayName => "Baldur's Gate 3";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3474);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [1086940u],
        GOGProductIds = [1456460669L],
    };

    private BaldursGate3Settings? _settings;

    public override bool DoFullGameBackup =>
        (_settings ??= Provider.GetRequiredService<ISettingsManager>().Get<BaldursGate3Settings>()).DoFullGameBackup;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> LocationNames => Bg3Constants.LocationNames;

    private static readonly RelativePath Launcher = RelativePath.FromUnsanitizedInput("Launcher/LariLauncher.exe");
    private static readonly RelativePath NativeExecutable = RelativePath.FromUnsanitizedInput("bin/bg3");

    protected override LarianGameProfile CreateProfile() => new(
        GameName: DisplayName,
        LarianRoot: KnownPath.LocalApplicationDataDirectory,
        LarianFolder: RelativePath.FromUnsanitizedInput("Larian Studios/Baldur's Gate 3"),
        ModsFolder: RelativePath.FromUnsanitizedInput("Mods"),
        Profiles: new LarianProfileLayout.Fixed(RelativePath.FromUnsanitizedInput("PlayerProfiles/Public")),
        ProfileSetting: _ => "",
        GameData: RelativePath.FromUnsanitizedInput("Data"),
        Executables: [Launcher, NativeExecutable],
        PakVersions: [15, 16, 18],
        Schema: new Bg3ModsettingsSchema(),
        GameModuleUuids: LsxXmlFormat.DependencyUuidsToIgnore.ToHashSet(StringComparer.OrdinalIgnoreCase),
        ScriptExtender: new LarianScriptExtender(
            DllPath: Bg3Constants.BG3SEGamePath,
            Override: new WineDllOverride("DWrite", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
            ConfigEntryName: "ScriptExtender/Config.json",
            ConfigFolder: RelativePath.FromUnsanitizedInput("ScriptExtender"),
            NexusModId: 2172),
        HasAdventureModules: false,
        VarietyId: SortOrderVarietyId.From(new Guid("A3D1F6B2-5C84-4E0B-9F27-6B1D0C8E2A55")),
        GameInstalledRecognizer: new Bg3InGameModRecognizer(),
        ExtraModsSourceFolderNames: []);

    // The official Linux build runs on the host, so a Steam install that also has a Proton prefix from earlier
    // runs is still the native game when bin/bg3 is installed.
    public override bool HasNativeLinuxBuild => true;

    public override RelativePath? NativeLinuxPrimaryFile => NativeExecutable;

    public override GamePath GetPrimaryFile(GameInstallation installation)
    {
        if (LarianBuild.IsNative(installation.LocatorResult)) return new GamePath(LocationId.Game, NativeExecutable);

        // Use launcher to allow choosing between DirectX11 and Vulkan on GOG, Steam already always starts the launcher
        return new GamePath(LocationId.Game, Launcher);
    }
}
