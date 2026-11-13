using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSinEE;

public class DivinityOriginalSinEE(IServiceProvider provider) : ALarianGame<DivinityOriginalSinEE>(provider), IGameData<DivinityOriginalSinEE>
{
    public static GameId GameId { get; } = GameId.From("Larian.DivinityOriginalSinEE");
    public static string DisplayName => "Divinity: Original Sin Enhanced Edition";

    // Most Enhanced Edition mods live on the older `divinityoriginalsin` domain (573), which this record does not
    // accept: nxm links from there will not land on this game. Only the `divinityoriginalsinenhancededition` domain is known.
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1995);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [373420u],
        GOGProductIds = [1445516929L, 1445524575L],
    };

    public override bool DoFullGameBackup =>
        Provider.GetRequiredService<ISettingsManager>().Get<DivinityOriginalSinEESettings>().DoFullGameBackup;

    public override IReadOnlyDictionary<string, string> LocationNames { get; } = new Dictionary<string, string>
    {
        [LarianConstants.ModsLocationId.ToString()] = "Mods",
        [LarianConstants.PlayerProfilesLocationId.ToString()] = "Profiles",
    };

    // HasNativeLinuxBuild stays false: true would make the Steam locator return a Linux result on the host file
    // system, and Documents would resolve to the real ~/Documents. A Steam install of the Linux depot is instead
    // located as a Windows build with an unready prefix, which the synchronizer refuses to apply to.
    protected override LarianGameProfile CreateProfile() => new(
        GameName: DisplayName,
        LarianRoot: KnownPath.MyDocumentsDirectory,
        LarianFolder: RelativePath.FromUnsanitizedInput("Larian Studios/Divinity Original Sin Enhanced Edition"),
        ModsFolder: RelativePath.FromUnsanitizedInput("Mods"),
        Profiles: new LarianProfileLayout.PerProfileFolder(RelativePath.FromUnsanitizedInput("PlayerProfiles")),
        ProfileSetting: _ => "",
        GameData: RelativePath.FromUnsanitizedInput("Data"),
        Executables: [RelativePath.FromUnsanitizedInput("Shipping/EoCApp.exe")],
        PakVersions: [9, 7],
        // Whether the game keeps a modsettings.lsx the app could write is not confirmed, so it writes none.
        Schema: null,
        GameModuleUuids: new HashSet<string>(),
        ScriptExtender: null,
        HasAdventureModules: false,
        // Reserved for the load order the game does not get yet.
        VarietyId: SortOrderVarietyId.From(new Guid("E079B484-35E8-463C-8B25-99719C0F999B")),
        GameInstalledRecognizer: null,
        ExtraModsSourceFolderNames: ["DOCS_MOD"],
        NativeLinuxDepotUnsupported: true);
}
