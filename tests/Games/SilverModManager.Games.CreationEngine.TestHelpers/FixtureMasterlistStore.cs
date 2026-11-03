using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.TestFramework;

namespace SilverModManager.Games.CreationEngine.TestHelpers;

/// <summary>
/// Serves the masterlist a test sets, so no test reads the bundled lists or the user's cache. The prelude is the
/// bundled one: it defines the default group every plugin belongs to, so a test list only needs its own entries.
/// </summary>
public sealed class FixtureMasterlistStore : IMasterlistStore
{
    private static readonly string Prelude = new BundledMasterlistStore().PreludeText();

    public string Masterlist { get; set; } = "plugins: []\n";
    public string Revision { get; set; } = "fixture 1";
    public LootGame? LastGame { get; private set; }

    /// <summary>
    /// Registers the store as the game's masterlist source, and a throwaway LOOT data folder: without it the
    /// variety would look for a userlist in the user's real data folder.
    /// </summary>
    public static IServiceCollection AddFixtureMasterlist(IServiceCollection services) => services
        .AddSingleton<FixtureMasterlistStore>()
        .AddSingleton<IMasterlistStore>(s => s.GetRequiredService<FixtureMasterlistStore>())
        .AddSingleton(s => new LootDataDirectory(s.GetRequiredService<TemporaryFileManager>().CreateFolder().Path));

    public MasterlistContent Current(LootGame game)
    {
        LastGame = game;
        return new MasterlistContent(Masterlist, Prelude, Revision);
    }
}
