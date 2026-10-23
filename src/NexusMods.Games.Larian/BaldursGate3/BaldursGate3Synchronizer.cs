using Microsoft.Extensions.DependencyInjection;

using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Larian.BaldursGate3.GameInstalled;
using NexusMods.Games.Larian.BaldursGate3.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.Larian.BaldursGate3;

public class BaldursGate3Synchronizer : ALoadoutSynchronizer
{
    private readonly BaldursGate3Settings _settings;

    private static GamePath GameFolder => new(LocationId.Game, "");
    private static GamePath PublicPlayerProfiles => new(Bg3Constants.PlayerProfilesLocationId, "");
    private static GamePath ModSettingsFile => new(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx");

    private readonly Bg3InGameModRecognizer _inGameModRecognizer = new();

    public BaldursGate3Synchronizer(IServiceProvider provider) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();
        _settings = settingsManager.Get<BaldursGate3Settings>();
    }

    protected override IGamePathFilter GameSpecificPathFilter { get; } = Abstractions.Loadouts.Synchronizers.GamePathFilters.Create(path =>
    {
        // ignore all files inside the public player profiles directory except the modsettings.lsx file
        if (path.InFolder(PublicPlayerProfiles)) return !path.Equals(ModSettingsFile);
        return false;
    });

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup) return false;
        return path.InFolder(GameFolder) || (path.InFolder(PublicPlayerProfiles) && path.Path != ModSettingsFile.Path);
    }

    protected override IGameInstalledContentRecognizer? GameInstalledContent => _inGameModRecognizer;

    /// <summary>
    /// Stores the <c>modsettings.lsx</c> entry of a pak the game installed. A pak the game switched off loses
    /// its entry there, and the writer needs the stored copy to bring it back, so an item without an entry this
    /// time keeps the one it has. Re-adding an unchanged value adds no datom.
    /// </summary>
    protected override void WriteGameInstalledItemExtras(ITransaction tx, EntityId itemId, GameInstalledContent content)
    {
        if (content.Extra is not ModsettingsDocument.ModuleEntry entry) return;

        tx.Add(itemId, Bg3ModuleEntry.Folder, entry.Folder);
        tx.Add(itemId, Bg3ModuleEntry.Uuid, entry.Uuid);
        tx.Add(itemId, Bg3ModuleEntry.Version64, entry.Version64);
        tx.Add(itemId, Bg3ModuleEntry.PublishHandle, entry.PublishHandle);
        tx.Add(itemId, Bg3ModuleEntry.Md5, entry.Md5);
        tx.Add(itemId, Bg3ModuleEntry.ModuleName, entry.Name);
    }
}
