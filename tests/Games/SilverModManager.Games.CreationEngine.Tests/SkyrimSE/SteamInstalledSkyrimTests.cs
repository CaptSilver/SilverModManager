using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Games.TestFramework;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>
/// Skyrim Special Edition installed from a Steam library on a symlinked mount, with real plugin headers in its
/// Data folder, taken through the real service graph.
/// </summary>
public class SteamInstalledSkyrimTests(ITestOutputHelper helper) : ASteamInstalledGameTest<SteamInstalledSkyrimTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const uint AppId = 489830;
    private const uint GameDepotId = 489831;
    private const uint ContentDepotId = 489833;
    private const ulong GameManifestId = 5000000000000000001;
    private const ulong ContentManifestId = 5000000000000000002;

    private static readonly DiagnosticId MissingMasterId = new("NexusMods.Games.CreationEngine", number: 1);
    private static readonly DiagnosticId UnreadablePluginsId = new("NexusMods.Games.CreationEngine", number: 3);

    private static readonly string[] Archives =
    [
        "Data/Skyrim - Textures0.bsa", "Data/Skyrim - Textures1.bsa", "Data/Skyrim - Meshes0.bsa", "Data/Skyrim - Sounds.bsa",
        "Data/Skyrim - Animations.bsa", "Data/Skyrim - Interface.bsa", "Data/Skyrim - Misc.bsa", "Data/Skyrim - Shaders.bsa",
    ];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddCreationEngine();
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var plugins = new List<SteamFile>
        {
            new("Data/Skyrim.esm", SkyrimPluginFile.Bytes("Skyrim.esm")),
            new("Data/Update.esm", SkyrimPluginFile.Bytes("Update.esm", "Skyrim.esm")),
            new("Data/Dawnguard.esm", SkyrimPluginFile.Bytes("Dawnguard.esm", "Skyrim.esm")),
        };
        var game = new SteamDepot(GameDepotId, GameManifestId,
        [
            new SteamFile("SkyrimSE.exe", "skyrim executable"),
            new SteamFile("SkyrimSELauncher.exe", "launcher"),
            ..plugins,
            ..Archives.Select(a => new SteamFile(a, $"archive {a}")),
        ]);
        var content = new SteamDepot(ContentDepotId, ContentManifestId, [new SteamFile("Data/Video/BGS_Logo.bik", "logo video")], ManifestCache.Library);

        var install = library.AddGame(AppId, "Skyrim Special Edition", "The Elder Scrolls V: Skyrim Special Edition", 16000000, game, content);

        // Files that are not the store's: a mod with its master present, a mod whose master is gone, a
        // Creation Club item the game downloaded itself, and a script extender log.
        SteamLibraryFixture.WriteExtra(install, "Data/Present.esp", SkyrimPluginFile.Bytes("Present.esp", "Skyrim.esm"));
        SteamLibraryFixture.WriteExtra(install, "Data/Orphan.esp", SkyrimPluginFile.Bytes("Orphan.esp", "Skyrim.esm", "Absent.esm"));
        SteamLibraryFixture.WriteExtra(install, "Data/ccBGSSSE001-Fish.esm", SkyrimPluginFile.Bytes("ccBGSSSE001-Fish.esm", "Skyrim.esm"));
        SteamLibraryFixture.WriteExtra(install, "Data/ccBGSSSE001-Fish.bsa", "creation club archive");
        SteamLibraryFixture.WriteExtra(install, "Data/SKSE/skse64.log", "log line");
        return install;
    }

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    [Fact]
    public async Task TheBaselineKeepsTheArchivesTheGameKeepsOutOfBackups()
    {
        await CreateLoadout();

        var baseline = LocalBaseline.All(Hashes.Local).Should().ContainSingle().Subject;
        baseline.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        baseline.Files.Select(f => f.Path.ToString()).Should().Contain(Archives, "a game's own archives are vanilla files even though backups skip them");
        baseline.Files.Should().HaveCount(2 + 3 + Archives.Length + 1);
    }

    [Fact]
    public async Task HealthCheckNamesTheMissingMasterOfTheModThatHasOne()
    {
        var loadout = await CreateLoadout();

        var diagnostics = await RunEveryEmitter(loadout);

        // Only the file checks are the point; the bare prefix has nothing else to say about this game.
        diagnostics.Select(d => d.Id.ToString()).Should().Equal(
            "NexusMods.Games.CreationEngine: 1",
            "NexusMods.Games.FileHashes: 3");
        var missing = diagnostics.Where(d => d.Id == MissingMasterId).ToArray();
        var description = Describe(missing.Should().ContainSingle().Subject);
        description.Should().Contain("Orphan.esp").And.Contain("Absent.esm");
        description.Should().NotContain("Present.esp");
        diagnostics.Should().NotContain(d => d.Id == UnreadablePluginsId);
    }

    [Fact]
    public async Task ManagingTheGameWritesPluginsTxtWithThePluginsFoundInTheFolderButNotTheEarlyLoaders()
    {
        var loadout = await CreateLoadout();

        var pluginsTxt = GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);
        pluginsTxt.FileExists.Should().BeTrue("the first apply writes the load order");
        var enabled = (await pluginsTxt.ReadAllTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The game's own plugins are all early loaders, which the game loads without being told, so the
        // asterisk list leaves them out; the plugins found in the folder join the loadout during this sync and
        // are listed now, so they are not switched off until the next Apply.
        enabled.Should().Equal("# Generated by SilverModManager", "*ccBGSSSE001-Fish.esm", "*Orphan.esp", "*Present.esp");

        // The plugins the sync adopted reach the stored order from a background reconcile that starts when the
        // sync commits, so what is stored depends on how far it got. Wait for it to finish before reading.
        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<NexusMods.Games.CreationEngine.SortOrder.IPluginSortOrderVariety>().Single();
        string[] stored = [];
        await WaitUntil(() => (stored = variety.StoredOrder(Refresh(loadout)).Select(row => row.Name).ToArray()).Length >= 6);
        stored.Should().Equal("Skyrim.esm", "Update.esm", "Dawnguard.esm", "ccBGSSSE001-Fish.esm", "Orphan.esp", "Present.esp");
    }

    // Gives up quietly at the deadline so the assertion after it reports what was stored, not a cancellation.
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }
}
