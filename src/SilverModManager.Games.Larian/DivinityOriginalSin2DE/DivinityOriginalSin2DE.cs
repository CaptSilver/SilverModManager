using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Modsettings;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSin2DE;

public class DivinityOriginalSin2DE(IServiceProvider provider) : ALarianGame<DivinityOriginalSin2DE>(provider), IGameData<DivinityOriginalSin2DE>, IDxgiGame
{
    public static GameId GameId { get; } = GameId.From("Larian.DivinityOriginalSin2DE");
    public static string DisplayName => "Divinity: Original Sin 2 Definitive Edition";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(2569);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [435150u],
        GOGProductIds = [1584823040L],
    };

    public override bool DoFullGameBackup =>
        Provider.GetRequiredService<ISettingsManager>().Get<DivinityOriginalSin2DESettings>().DoFullGameBackup;

    public override IReadOnlyDictionary<string, string> LocationNames { get; } = new Dictionary<string, string>
    {
        [LarianConstants.ModsLocationId.ToString()] = "Mods",
        [LarianConstants.PlayerProfilesLocationId.ToString()] = "Profiles",
    };

    protected override LarianGameProfile CreateProfile() => new(
        GameName: "Divinity: Original Sin 2",
        LarianRoot: KnownPath.MyDocumentsDirectory,
        LarianFolder: RelativePath.FromUnsanitizedInput("Larian Studios/Divinity Original Sin 2 Definitive Edition"),
        ModsFolder: RelativePath.FromUnsanitizedInput("Mods"),
        Profiles: new LarianProfileLayout.PerProfileFolder(RelativePath.FromUnsanitizedInput("PlayerProfiles")),
        ProfileSetting: settings => settings.Get<DivinityOriginalSin2DESettings>().ProfileName,
        GameData: RelativePath.FromUnsanitizedInput("DefEd/Data"),
        Executables: [RelativePath.FromUnsanitizedInput("DefEd/bin/EoCApp.exe")],
        PakVersions: [13, 10],
        Schema: new Dos2ModsettingsSchema(),
        GameModuleUuids: Dos2GameModules.Uuids,
        ScriptExtender: null,
        HasAdventureModules: true,
        VarietyId: SortOrderVarietyId.From(new Guid("ECC16B09-DA03-4FF0-8F72-FD4353341E9E")),
        GameInstalledRecognizer: null,
        ExtraModsSourceFolderNames: []);
}
