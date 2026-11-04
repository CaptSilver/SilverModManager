using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.GeneratedCaches;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.GeneratedCaches;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using VR = SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// The caches FasterFileCopy and SkyrimPrecombines keep derived from the game files: when they apply, where they
/// live, what a change to the game files makes stale, and that an Apply clears them itself.
/// </summary>
public class GeneratedCacheTests(ITestOutputHelper helper) : AIsolatedGameTest<GeneratedCacheTests, VR>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<VR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static readonly GameId[] Games = [VR.GameId];

    private FasterFileCopyCache FileCopy => new(Games, NullLogger<FasterFileCopyCache>.Instance);
    private SkyrimPrecombinesCache Precombines => new(Games, NullLogger<SkyrimPrecombinesCache>.Instance);

    private static GamePath Game(string path) => new(LocationId.Game, path);
    private AbsolutePath OnDisk(string path) => GameInstallation.Locations.ToAbsolutePath(Game(path));
    private AbsolutePath Prefixed(string path) => GameInstallation.Locations[LocationId.AppData].Path.Parent / path;

    private static async Task Put(AbsolutePath file, string content = "x")
    {
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(content);
    }

    private const string FileCopyCache = "Data/SKSE/Plugins/FasterFileCopy_cache";

    private async Task WithFasterFileCopy(string? ini = null)
    {
        await Put(OnDisk("Data/SKSE/Plugins/FasterFileCopy.dll"));
        if (ini is not null) await Put(OnDisk("Data/SKSE/Plugins/FasterFileCopy.ini"), ini);
    }

    private async Task WithPrecombines(string? ini = null)
    {
        await Put(OnDisk("Data/SKSE/Plugins/SkyrimPrecombines.dll"));
        if (ini is not null) await Put(OnDisk("Data/SKSE/Plugins/SkyrimPrecombines.ini"), ini);
    }

    private static GamePath[] Changed(params string[] paths) => paths.Select(Game).ToArray();

    private static string[] Names(IEnumerable<AbsolutePath> items) => items.Select(i => i.FileName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task ACacheAppliesOnlyWhileItsDllIsDeployed()
    {
        FileCopy.AppliesTo(GameInstallation, []).Should().BeFalse();
        Precombines.AppliesTo(GameInstallation, []).Should().BeFalse();

        await WithFasterFileCopy();
        FileCopy.AppliesTo(GameInstallation, []).Should().BeTrue();
        Precombines.AppliesTo(GameInstallation, []).Should().BeFalse("the other mod's DLL is not there");

        await WithPrecombines();
        Precombines.AppliesTo(GameInstallation, []).Should().BeTrue();
    }

    [Fact]
    public void ACacheStillAppliesWhenTheApplyRemovedItsDll()
    {
        FileCopy.AppliesTo(GameInstallation, Changed("Data/SKSE/Plugins/FasterFileCopy.dll")).Should().BeTrue();
        FileCopy.AppliesTo(GameInstallation, Changed("Data/SKSE/Plugins/SkyrimPrecombines.dll")).Should().BeFalse("it is the other mod's DLL");
    }

    [Fact]
    public async Task ACacheStillAppliesWhileItsFolderIsLeftBehindWithoutTheDll()
    {
        await Put(OnDisk(FileCopyCache) / "Foo.bsa.decomp");
        FileCopy.AppliesTo(GameInstallation, []).Should().BeTrue();

        await Put(Prefixed("FasterObjects") / "1234abcd/meshes/a.nif");
        Precombines.AppliesTo(GameInstallation, []).Should().BeTrue();
    }

    [Fact]
    public async Task ACacheDoesNotApplyToAGameItWasNotRegisteredFor()
    {
        await WithFasterFileCopy();
        new FasterFileCopyCache([GameId.From("CreationEngine.SomethingElse")], NullLogger<FasterFileCopyCache>.Instance)
            .AppliesTo(GameInstallation, []).Should().BeFalse();
    }

    [Fact]
    public async Task FasterFileCopyKeepsItsCacheBesideThePluginUnlessTheIniMovesIt()
    {
        await WithFasterFileCopy();
        FileCopy.OwnedFolders(GameInstallation).Should().Equal(OnDisk(FileCopyCache));
        FileCopy.IsOwnedPath(Game(FileCopyCache + "/Skyrim - Meshes.bsa.decomp")).Should().BeTrue();
        FileCopy.IsOwnedPath(Game("Data/SKSE/Plugins/FasterFileCopy.dll")).Should().BeFalse();
        FileCopy.IsOwnedPath(Game("Data/SKSE/Plugins/FasterFileCopy_cache_other/x")).Should().BeFalse();
    }

    [Fact]
    public async Task FasterFileCopyFollowsAnIniFolderGivenAsAWindowsPathInThePrefix()
    {
        await WithFasterFileCopy("[General]\r\nsCacheDir=C:\\users\\steamuser\\ffc cache\r\n");

        var folder = FileCopy.OwnedFolders(GameInstallation).Should().ContainSingle().Subject;

        folder.FileName.Should().Be("ffc cache");
        folder.ToString().Should().Contain("/drive_c/users/steamuser/", "C: is the prefix's drive_c");
    }

    [Fact]
    public async Task FasterFileCopyFollowsAnIniFolderOnTheZDriveInAnySection()
    {
        await WithFasterFileCopy("[Other]\r\nsCacheDir=Z:\\var\\tmp\\ffc\r\n");

        FileCopy.OwnedFolders(GameInstallation).Select(f => f.ToString()).Should().Equal("/var/tmp/ffc");
    }

    [Fact]
    public async Task ARelativeIniFolderIsNotFollowed()
    {
        await WithFasterFileCopy("[General]\r\nsCacheDir=cache\r\n");

        FileCopy.OwnedFolders(GameInstallation).Should().Equal(OnDisk(FileCopyCache));
    }

    [Fact]
    public async Task PrecombinesKeepsItsCacheInTheLocalAppDataOfThePrefixUnlessTheIniMovesIt()
    {
        await WithPrecombines("[General]\r\nbRuntimeBake=1\r\n");
        Precombines.OwnedFolders(GameInstallation).Should().Equal(Prefixed("FasterObjects"));
        Prefixed("FasterObjects").ToString().Should().Contain("/drive_c/users/").And.Contain("/AppData/Local/FasterObjects");

        await WithPrecombines("[General]\r\nbRuntimeBake=1\r\nsCacheFolder=C:\\users\\steamuser\\baked\r\n");
        var folder = Precombines.OwnedFolders(GameInstallation).Should().ContainSingle().Subject;
        folder.FileName.Should().Be("baked");
        folder.ToString().Should().Contain("/drive_c/users/steamuser/");
    }

    [Fact]
    public async Task FasterFileCopyDeletesExactlyTheChangedAndRemovedArchivesEntries()
    {
        await WithFasterFileCopy();
        var cache = OnDisk(FileCopyCache);
        foreach (var name in new[] { "Changed.bsa.decomp", "Changed.bsa.decomp.lock", "Gone.bsa.decomp", "Gone.bsa.decomp.lock", "Kept.bsa.decomp", "Kept.bsa.decomp.lock", "Note.txt" })
            await Put(cache / name);

        var stale = FileCopy.StaleItems(GameInstallation, Changed("Data/Changed.bsa", "Data/Gone.bsa", "Data/Other.bsa"));

        Names(stale).Should().Equal("Changed.bsa.decomp", "Changed.bsa.decomp.lock", "Gone.bsa.decomp", "Gone.bsa.decomp.lock");
    }

    [Fact]
    public async Task FasterFileCopyMatchesTheArchiveNameIgnoringCase()
    {
        await WithFasterFileCopy();
        await Put(OnDisk(FileCopyCache) / "Skyrim - Meshes0.bsa.decomp");

        Names(FileCopy.StaleItems(GameInstallation, Changed("Data/skyrim - meshes0.bsa"))).Should().Equal("Skyrim - Meshes0.bsa.decomp");
    }

    [Theory]
    [InlineData("Data/textures/a.dds")]
    [InlineData("Data/Some.esp")]
    [InlineData("Data/SKSE/Plugins/FasterFileCopy.ini")]
    public async Task FasterFileCopyLeavesItsCacheAloneForAChangeThatIsNotAnArchive(string changed)
    {
        await WithFasterFileCopy();
        await Put(OnDisk(FileCopyCache) / "A.bsa.decomp");

        FileCopy.StaleItems(GameInstallation, Changed(changed)).Should().BeEmpty();
    }

    [Fact]
    public void AMissingCacheFolderIsFine()
    {
        FileCopy.StaleItems(GameInstallation, Changed("Data/A.bsa")).Should().BeEmpty();
        Precombines.StaleItems(GameInstallation, Changed("Data/A.bsa")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Data/Some.esp")]
    [InlineData("Data/Some.ESM")]
    [InlineData("Data/Some.esl")]
    [InlineData("Data/Meshes/architecture/wall.nif")]
    [InlineData("Data/Skyrim - Meshes0.bsa")]
    [InlineData("Data/Some.ba2")]
    public async Task PrecombinesClearsItsWholeCacheWhenAPluginAMeshOrAnArchiveChanged(string changed)
    {
        await WithPrecombines();
        var cache = Prefixed("FasterObjects");
        await Put(cache / "1234abcd/meshes/a.nif");
        await Put(cache / "1234abcd/RuntimeBake/b.bin");
        await Put(cache / "loose.dat");
        await Put(cache / "notes/readme.txt");

        var stale = Precombines.StaleItems(GameInstallation, Changed(changed));

        Names(stale).Should().Equal("1234abcd");
    }

    [Theory]
    [InlineData("Data/textures/a.dds")]
    [InlineData("Data/SKSE/Plugins/SkyrimPrecombines.ini")]
    [InlineData("Data/SKSE/Plugins/Other.esp")]
    [InlineData("Data/Scripts/a.pex")]
    public async Task PrecombinesKeepsItsCacheForAChangeThatCannotAffectTheBake(string changed)
    {
        await WithPrecombines();
        await Put(Prefixed("FasterObjects") / "1234abcd/meshes/a.nif");

        Precombines.StaleItems(GameInstallation, Changed(changed)).Should().BeEmpty();
    }

    [Fact]
    public async Task PrecombinesNeverEmptiesAFolderHoldingTheGame()
    {
        // An ini that names the game folder itself would otherwise wipe the game.
        await WithPrecombines($"[General]\r\nsCacheFolder=Z:{GameInstallation.Locations[LocationId.Game].Path.ToString().Replace('/', '\\')}\r\n");

        Precombines.StaleItems(GameInstallation, Changed("Data/a.esp")).Should().BeEmpty();
        OnDisk("Data/SKSE/Plugins/SkyrimPrecombines.dll").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task PrecombinesNeverEmptiesAFolderThatReachesTheGameThroughALink()
    {
        // The ini names a path that is not under any location as a string, but is the game folder once links are followed.
        var link = GameInstallation.Locations[LocationId.Game].Path.Parent.Parent / "gamelink-for-test";
        Directory.CreateSymbolicLink(link.ToString(), GameInstallation.Locations[LocationId.Game].Path.ToString());
        try
        {
            await Put(OnDisk("Data/1234abcd/meshes/a.nif"));
            await WithPrecombines($"[General]\r\nsCacheFolder=Z:{(link / "Data").ToString().Replace('/', '\\')}\r\n");

            Precombines.StaleItems(GameInstallation, Changed("Data/a.esp")).Should().BeEmpty();
            OnDisk("Data/1234abcd/meshes/a.nif").FileExists.Should().BeTrue();
        }
        finally
        {
            File.Delete(link.ToString());
        }
    }

    [Fact]
    public async Task PrecombinesNeverEmptiesAFolderInsideTheGameFolder()
    {
        await Put(OnDisk("Data/1234abcd/meshes/a.nif"));
        await WithPrecombines($"[General]\r\nsCacheFolder=Z:{OnDisk("Data").ToString().Replace('/', '\\')}\r\n");

        Precombines.StaleItems(GameInstallation, Changed("Data/a.esp")).Should().BeEmpty();
    }

    [Fact]
    public async Task PrecombinesNeverEmptiesAFolderThatHoldsTheGameFolder()
    {
        var holder = GameInstallation.Locations[LocationId.Game].Path.Parent.Parent;
        await WithPrecombines($"[General]\r\nsCacheFolder=Z:{holder.ToString().Replace('/', '\\')}\r\n");

        Precombines.StaleItems(GameInstallation, Changed("Data/a.esp")).Should().BeEmpty();
    }

    [Fact]
    public async Task AnIniValueThatCannotBeAPathNeverFailsTheClear()
    {
        await WithPrecombines("[General]\r\nsCacheFolder=Z:\\bad\0folder\r\n");
        var clearer = new GeneratedCacheClearer([Precombines], NullLogger.Instance);

        var act = () => clearer.Clear(GameInstallation, Changed("Data/a.esp"));

        act.Should().NotThrow();
    }

    private async Task<Loadout.ReadOnly> ApplyMod(params string[] files)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, files.Select(f => (RelativePath)f), loadout.LoadoutId, "A mod");
        await tx.Commit();
        return await Synchronizer.Synchronize(Refresh(loadout));
    }

    [Fact]
    public async Task AnApplyClearsTheEntriesOfTheArchivesItChangedAndLeavesTheOthers()
    {
        await WithFasterFileCopy();
        var cache = OnDisk(FileCopyCache);
        foreach (var name in new[] { "Changed.bsa.decomp", "Changed.bsa.decomp.lock", "Kept.bsa.decomp" }) await Put(cache / name);

        await ApplyMod("Data/Changed.bsa");

        cache.EnumerateFiles().Select(f => f.FileName).Should().Equal("Kept.bsa.decomp");
        OnDisk("Data/Changed.bsa").FileExists.Should().BeTrue();
    }

    [Theory]
    [InlineData("Data/Meshes/architecture/wall.nif")]
    [InlineData("Data/Patch.ba2")]
    public async Task AnApplyThatChangesAMeshOrAnArchiveClearsThePrecombinesCache(string changed)
    {
        await WithPrecombines();
        var cache = Prefixed("FasterObjects");
        await Put(cache / "1234abcd/meshes/a.nif");

        await ApplyMod(changed);

        cache.EnumerateFiles(recursive: true).Should().BeEmpty();
    }

    [Fact]
    public async Task AnApplyThatChangesOnlyATextureLeavesBothCachesAlone()
    {
        await WithFasterFileCopy();
        await WithPrecombines();
        await Put(OnDisk(FileCopyCache) / "A.bsa.decomp");
        await Put(Prefixed("FasterObjects") / "1234abcd/a.nif");

        await ApplyMod("Data/textures/a.dds");

        OnDisk(FileCopyCache + "/A.bsa.decomp").FileExists.Should().BeTrue();
        (Prefixed("FasterObjects") / "1234abcd/a.nif").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AnApplyThatFailsWhileWritingLeavesTheCachesAlone()
    {
        await WithFasterFileCopy();
        await Put(OnDisk(FileCopyCache) / "Changed.bsa.decomp");
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"Data/Changed.bsa"], loadout.LoadoutId, "A mod");
            await tx.Commit();
        }

        // Passes the preflight, then cannot create the archive: Data takes no new files.
        var data = OnDisk("Data").ToString();
        var mode = File.GetUnixFileMode(data);
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

            await apply.Should().ThrowAsync<Exception>();
        }
        finally
        {
            File.SetUnixFileMode(data, mode);
        }

        OnDisk("Data/Changed.bsa").FileExists.Should().BeFalse("the write is what failed");
        OnDisk(FileCopyCache + "/Changed.bsa.decomp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AFailedPreflightLeavesTheCachesAlone()
    {
        await WithFasterFileCopy();
        await Put(OnDisk(FileCopyCache) / "Changed.bsa.decomp");
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            // The archive is fine; the second file's path climbs out of the game folder, which the Apply refuses.
            await AddModAsync(tx, [(RelativePath)"Data/Changed.bsa", (RelativePath)"Data/../../outside.esp"], loadout.LoadoutId, "Evil mod");
            await tx.Commit();
        }

        var apply = async () => await Synchronizer.Synchronize(Refresh(loadout));

        await apply.Should().ThrowAsync<SynchronizerPreflightException>();
        OnDisk(FileCopyCache + "/Changed.bsa.decomp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task AnApplyClearsTheCacheOfAModWhoseDllIsGone()
    {
        // FasterFileCopy was disabled earlier and left its cache behind; the archive changes while the DLL is absent.
        await Put(OnDisk(FileCopyCache) / "Foo.bsa.decomp");
        await Put(OnDisk(FileCopyCache) / "Kept.bsa.decomp");

        await ApplyMod("Data/Foo.bsa");

        OnDisk(FileCopyCache + "/Foo.bsa.decomp").FileExists.Should().BeFalse();
        OnDisk(FileCopyCache + "/Kept.bsa.decomp").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ACacheAnEarlierBuildIndexedStaysOnDiskAndItsRowsAreDropped()
    {
        await WithFasterFileCopy();
        var cacheFile = Game(FileCopyCache + "/Kept.bsa.decomp");
        // Content equal to the path text, which is what the Overrides row below hashes.
        await Put(OnDisk(cacheFile.Path.ToString()), cacheFile.Path.ToString());

        var loadout = await CreateLoadout();
        await AddToOverrides(loadout, cacheFile.Path.ToString());
        var bytes = System.Text.Encoding.UTF8.GetBytes(cacheFile.Path.ToString());
        using (var tx = Connection.BeginTransaction())
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = cacheFile.ToGamePathParentTuple(loadout.Installation),
                Identity = FileIdentity.XxHash3(bytes.xxHash3()),
                XxHash3 = bytes.xxHash3(),
                Size = Size.FromLong(bytes.Length),
                LastModified = DateTime.UtcNow,
                GameId = loadout.Installation,
            };
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadout, loadout.Id);
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
            tx.Add(loadout.Installation.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
            await tx.Commit();
        }

        var applied = await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk(FileCopyCache + "/Kept.bsa.decomp").FileExists.Should().BeTrue("the cache is the mod's, not the loadout's to delete");
        LoadoutOverridesGroup.FindByOverridesFor(applied.Db, applied.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(file => ((GamePath)file.TargetPath).Path.ToString())
            .Should().NotContain(path => path.Contains("FasterFileCopy_cache"), "the row an earlier build filed is dropped");
        DiskStateEntry.FindByGame(applied.Db, applied.Installation).Select(e => ((GamePath)e.Path).Path.ToString())
            .Should().NotContain(path => path.Contains("FasterFileCopy_cache"));
    }

    [Fact]
    public async Task TheCacheFolderIsNeverIngestedIntoTheOverridesAndStaysOnDisk()
    {
        await WithFasterFileCopy();
        await Put(OnDisk(FileCopyCache) / "Kept.bsa.decomp", "cached");
        await Put(OnDisk("Data/SKSE/Plugins/Player.ini"), "mine");

        var loadout = await ApplyMod("Data/textures/a.dds");

        var overrides = LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Select(file => ((GamePath)file.TargetPath).Path.ToString())
            .ToArray();
        overrides.Should().Contain("Data/SKSE/Plugins/Player.ini", "an ordinary file there is still the player's");
        overrides.Should().NotContain(path => path.Contains("FasterFileCopy_cache"));
        OnDisk(FileCopyCache + "/Kept.bsa.decomp").FileExists.Should().BeTrue();
    }
}
