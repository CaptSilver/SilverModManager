using System.Text;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests;

public class EldenRingIndexingTests(ITestOutputHelper helper) : AEldenRingTest<EldenRingIndexingTests>(helper)
{
    private string[] IndexedPaths()
    {
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        return DiskStateEntry.FindByGame(Connection.Db, metadata).Select(e => ((GamePath)e.Path).Path.ToString()).Order().ToArray();
    }

    private LoadoutItemWithTargetPath.ReadOnly[] OverridesChildren(Loadout.ReadOnly loadout)
    {
        return LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .ToArray();
    }

    [Fact]
    public async Task TheSmallFilesOfTheGameFolderAreIndexedAndTheArchivesAreNot()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        var indexed = IndexedPaths();

        indexed.Should().Contain(EldenRingInstall.IndexedGameFiles);
        indexed.Should().NotContain(p => p.EndsWith(".bdt") || p.EndsWith(".bhd"));
        indexed.Should().NotContain(p => p.StartsWith("Game/sd/") || p.StartsWith("Game/movie"));
    }

    [Fact]
    public async Task TheArtbookFoldersBesideTheGameFolderAreIndexedLikeAnyOtherFile()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        IndexedPaths().Should().Contain(["ArtbookOST/readme.txt", "ERD_ArtbookOST/readme.txt"]);
    }

    [Fact]
    public void EveryPathInTheGameFolderIsIgnoredWhenBackingUp()
    {
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "Game/regulation.bin")).Should().BeTrue();
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "Game/mods/Some/dir/file.dll")).Should().BeTrue();
        Synchronizer.IsIgnoredBackupPath(new GamePath(LocationId.Game, "ArtbookOST/readme.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task AModifiedGameFileIsNeverCopiedIntoTheFileStore()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var modified = Encoding.UTF8.GetBytes("a regulation file somebody edited");
        await (RealGameFolder / "regulation.bin").WriteAllBytesAsync(modified);

        await Synchronizer.Synchronize(Refresh(loadout));

        (await FileStore.HaveFile(modified.xxHash3())).Should().BeFalse("nothing under the game folder is backed up");
        OverridesChildren(Refresh(loadout)).Should().BeEmpty();
        (await (RealGameFolder / "regulation.bin").ReadAllBytesAsync()).Should().Equal(modified);
    }

    [Fact]
    public async Task AFileDroppedIntoTheGameFolderIsNotIngestedIntoTheOverrides()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));
        var before = SnapshotOfGameFolder();

        WriteIntoInstall("Game/dinput8.dll", "a hook somebody dropped in");
        var withTheNewFile = SnapshotOfGameFolder();
        await Synchronizer.Synchronize(Refresh(loadout));

        OverridesChildren(Refresh(loadout)).Should().BeEmpty();
        SnapshotOfGameFolder().Should().Equal(withTheNewFile);
        withTheNewFile.Should().NotEqual(before);
    }

    private void WriteIntoInstall(string path, string text)
    {
        var file = Install.Folder / path;
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), text);
    }
}
