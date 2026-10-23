using Microsoft.Extensions.DependencyInjection;

using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.StardewValley;

public class StardewValleyLoadoutSynchronizer : ALoadoutSynchronizer
{
    public StardewValleyLoadoutSynchronizer(IServiceProvider provider) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();
        _settings = settingsManager.Get<StardewValleySettings>();
    }

    /// <summary>
    /// The content folder of the game, we ignore files in this folder
    /// </summary>
    private static readonly GamePath ContentFolder = new(LocationId.Game, "Content");

    private readonly StardewValleySettings _settings;

    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup) return false;
        if (path.LocationId != LocationId.Game) return false;
        return path.Path.InFolder(ContentFolder.Path);
    }
}
