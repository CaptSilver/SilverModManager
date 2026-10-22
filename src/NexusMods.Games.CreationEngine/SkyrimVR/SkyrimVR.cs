using DynamicData.Kernel;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.SkyrimVR;

public class SkyrimVR(IServiceProvider provider) : ACreationEngineGame<SkyrimVR>(provider), IGameData<SkyrimVR>
{
    public static GameId GameId { get; } = GameId.From("CreationEngine.SkyrimVR");
    public static string DisplayName => "Skyrim VR";
    // Skyrim VR has no Nexus domain of its own: it shares Skyrim SE's id, and its mods live there.
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(1704);

    public override StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        // https://github.com/ModOrganizer2/modorganizer-game_skyrimVR/blob/master/src/gameskyrimvr.cpp (steamAPPId)
        SteamAppIds = [611670u],
    };

    protected override RelativePath PrimaryExe => "SkyrimVR.exe";
    public override PluginFormat HeaderFormat => PluginFormat.Tes4;
    public override GamePath PluginsFile => new(LocationId.AppData, "plugins.txt");
    public override IPluginListMechanism PluginList(GameInstallation installation) => Mechanism;
    private static readonly IPluginListMechanism Mechanism = new AsteriskMechanism();
    public override IReadOnlyList<string> HardcodedPlugins => ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm"];
    public override bool SupportsLightPlugins => true;
    public override bool MasterByExtension => true;

    protected override string LocalAppDataFolderName(GameLocatorResult gameLocatorResult) => "Skyrim VR";
    protected override string MyGamesFolderName(GameLocatorResult gameLocatorResult) => "Skyrim VR";

    protected override string[] GameAliases => ["Skyrim VR", "SkyrimVR"];
    protected override string[] StopPatterns => ["(^|/)skse(/|$)"];
    protected override string[] EngineFiles => [@"sksevr_loader\.exe", @"sksevr_.*\.dll"];
}
