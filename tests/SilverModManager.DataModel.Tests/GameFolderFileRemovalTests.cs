using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Overrides;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using System.Text;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Taking a file out of the game folder: the store has its bytes first, and it goes to the trash, or is deleted when
/// the trash is on another file system.
/// </summary>
public class GameFolderFileRemovalTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GameFolderFileRemovalTests>(helper)
{
    private static readonly GamePath PathA = new(LocationId.Game, "bin/x64/plugins/a.dll");

    [Fact]
    public async Task BacksUpBytesTheStoreLacks()
    {
        var loadout = await CreateLoadout();
        var identity = await WriteToGameFolder(loadout, PathA, "bytes only on disk");
        (await FileStore.HaveFile(identity)).Should().BeFalse("the fixture wrote the file without storing it");

        await Removal().EnsureStored(loadout, [(PathA, identity)], CancellationToken.None);

        (await FileStore.HaveFile(identity)).Should().BeTrue();
        Absolute(loadout, PathA).FileExists.Should().BeTrue("storing does not remove anything");
    }

    [Fact]
    public async Task RefusesAFileThatChangedOnDisk()
    {
        var loadout = await CreateLoadout();
        var recorded = FileIdentity.XxHash3(Encoding.UTF8.GetBytes("what the row says").xxHash3());
        await WriteToGameFolder(loadout, PathA, "what the disk has now");

        var act = () => Removal().EnsureStored(loadout, [(PathA, recorded)], CancellationToken.None);

        (await act.Should().ThrowAsync<GameFolderFileRemovalRefusedException>()).Which.Message.Should().Contain("a.dll");
        (await FileStore.HaveFile(recorded)).Should().BeFalse();
        Absolute(loadout, PathA).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task RefusesAFileThatIsGoneWhenTheStoreHasNoCopy()
    {
        var loadout = await CreateLoadout();
        var identity = FileIdentity.XxHash3(Encoding.UTF8.GetBytes("never stored").xxHash3());

        var act = () => Removal().EnsureStored(loadout, [(PathA, identity)], CancellationToken.None);

        await act.Should().ThrowAsync<GameFolderFileRemovalRefusedException>();
    }

    [Fact]
    public async Task AChangeOnDiskIsRefusedBeforeAnyOtherFileIsBackedUp()
    {
        var loadout = await CreateLoadout();
        var other = new GamePath(LocationId.Game, "bin/x64/plugins/b.dll");
        var good = await WriteToGameFolder(loadout, other, "good");
        var recorded = FileIdentity.XxHash3(Encoding.UTF8.GetBytes("recorded").xxHash3());
        await WriteToGameFolder(loadout, PathA, "different");

        var act = () => Removal().EnsureStored(loadout, [(other, good), (PathA, recorded)], CancellationToken.None);

        await act.Should().ThrowAsync<GameFolderFileRemovalRefusedException>();
        (await FileStore.HaveFile(good)).Should().BeFalse("nothing is written when any file is refused");
    }

    [Fact]
    public async Task TrashesOnTheSameFileSystem()
    {
        var loadout = await CreateLoadout();
        await WriteToGameFolder(loadout, PathA, "trash me");
        var dataHome = SiblingOfGameFolder(loadout, "xdg-data");

        var result = await Removal(new FreedesktopTrash(dataHome)).TakeOffDisk(loadout, [PathA], CancellationToken.None);

        result.Trashed.Should().Equal(PathA);
        result.Deleted.Should().BeEmpty();
        Absolute(loadout, PathA).FileExists.Should().BeFalse();
        File.Exists(Path.Combine(dataHome, "Trash", "info", "a.dll.trashinfo")).Should().BeTrue();
        File.Exists(Path.Combine(dataHome, "Trash", "files", "a.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task DeletesWhenTheTrashRefusesACrossDeviceMove()
    {
        var loadout = await CreateLoadout();
        await WriteToGameFolder(loadout, PathA, "delete me");
        var trash = new FreedesktopTrash(SiblingOfGameFolder(loadout, "xdg-data"), TimeProvider.System,
            (_, _) => throw new IOException("the folders are on different file systems"));

        var result = await Removal(trash).TakeOffDisk(loadout, [PathA], CancellationToken.None);

        result.Deleted.Should().Equal(PathA);
        result.Trashed.Should().BeEmpty();
        Absolute(loadout, PathA).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task APathWithNoFileOnDiskIsSkipped()
    {
        var loadout = await CreateLoadout();

        var result = await Removal(new FreedesktopTrash(SiblingOfGameFolder(loadout, "xdg-data"))).TakeOffDisk(loadout, [PathA], CancellationToken.None);

        result.Trashed.Should().BeEmpty();
        result.Deleted.Should().BeEmpty();
    }

    private GameFolderFileRemoval Removal(ITrash? trash = null) =>
        new(FileStore, trash ?? ServiceProvider.GetRequiredService<ITrash>(), NullLogger<GameFolderFileRemoval>.Instance);

    private static AbsolutePath Absolute(Loadout.ReadOnly loadout, GamePath path) => loadout.InstallationInstance.Locations.ToAbsolutePath(path);

    private static string SiblingOfGameFolder(Loadout.ReadOnly loadout, string name) =>
        Path.Combine(Absolute(loadout, new GamePath(LocationId.Game, "")).Parent.GetFullPath(), name);

    private static async Task<FileIdentity> WriteToGameFolder(Loadout.ReadOnly loadout, GamePath path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var file = Absolute(loadout, path);
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
        return FileIdentity.XxHash3(bytes.xxHash3());
    }
}
