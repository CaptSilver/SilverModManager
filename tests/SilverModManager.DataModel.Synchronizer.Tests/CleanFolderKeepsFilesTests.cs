using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Cleaning the game folder takes unknown files off disk, but never loses them: they move into a switched-off group
/// in My Mods, where the user can see them and switch them back on.
/// </summary>
public class CleanFolderKeepsFilesTests(ITestOutputHelper helper) : AStubbedGameTest<CleanFolderKeepsFilesTests>(helper)
{
    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private async Task<Loadout.ReadOnly> LoadoutWithUnknownFiles()
    {
        var loadout = await CreateLoadout();
        foreach (var (path, content) in new[] { ("Mods/Cool/a.pak", "a"), ("Mods/Cool/b.pak", "b"), ("crash.dmp", "dump") })
        {
            (GameFolder / path).Parent.CreateDirectory();
            await (GameFolder / path).WriteAllTextAsync(content);
        }

        return await Synchronizer.Synchronize(loadout);
    }

    private static LoadoutItem.ReadOnly[] FoundGroups(Loadout.ReadOnly loadout)
        => FoundInGameFolderGroup.All(loadout.Db)
            .Select(group => group.AsLoadoutItemGroup().AsLoadoutItem())
            .Where(item => item.LoadoutId == loadout.LoadoutId)
            .ToArray();

    private static LoadoutItem.ReadOnly FoundGroup(Loadout.ReadOnly loadout) => FoundGroups(loadout).Single();

    private static string[] FilesOf(LoadoutItem.ReadOnly group)
        => LoadoutItemGroup.Load(group.Db, group.Id).Children.OfTypeLoadoutItemWithTargetPath()
            .Select(file => ((GamePath)file.TargetPath).Path.ToString()).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task CleaningMovesTheUnknownFilesIntoASwitchedOffGroupInMyMods()
    {
        var loadout = await LoadoutWithUnknownFiles();

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        var fresh = Refresh(loadout);
        var group = FoundGroup(fresh);
        group.IsDisabled.Should().BeTrue();
        group.ParentId.Should().Be(LoadoutItemGroupId.From(DefaultCollection.Of(fresh.Db, fresh.LoadoutId)!.Value.Value));
        FilesOf(group).Should().Equal("Mods/Cool/a.pak", "Mods/Cool/b.pak", "crash.dmp");
        (GameFolder / "Mods/Cool/a.pak").FileExists.Should().BeFalse("the Apply takes the switched-off files out of the folder");
        (GameFolder / "crash.dmp").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingTheGroupBackOnPutsTheFilesBack()
    {
        var loadout = await LoadoutWithUnknownFiles();
        await LoadoutManager.CleanGameFolder(Refresh(loadout));
        var group = FoundGroup(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        (await (GameFolder / "Mods/Cool/a.pak").ReadAllTextAsync()).Should().Be("a");
        (await (GameFolder / "crash.dmp").ReadAllTextAsync()).Should().Be("dump");
    }

    [Fact]
    public async Task NothingIsLeftInTheExternalChangesAfterCleaning()
    {
        var loadout = await LoadoutWithUnknownFiles();

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        var fresh = Refresh(loadout);
        LoadoutOverridesGroup.FindByOverridesFor(fresh.Db, fresh.Id)
            .SelectMany(overrides => overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task CleaningAFolderWithNothingToCleanAddsNoGroup()
    {
        var loadout = await CreateLoadout();

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        FoundGroups(Refresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task CleaningAgainAddsToTheSameGroup()
    {
        var loadout = await LoadoutWithUnknownFiles();
        await LoadoutManager.CleanGameFolder(Refresh(loadout));
        await (GameFolder / "later.log").WriteAllTextAsync("later");
        await Synchronizer.Synchronize(Refresh(loadout));

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        var group = FoundGroup(Refresh(loadout));
        FilesOf(group).Should().Contain("later.log").And.Contain("crash.dmp");
    }

    [Fact]
    public async Task ACleanAfterTheGroupWasSwitchedOnTakesTheNewFilesOffDiskToo()
    {
        var loadout = await LoadoutWithUnknownFiles();
        await LoadoutManager.CleanGameFolder(Refresh(loadout));
        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(FoundGroup(Refresh(loadout)).Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        await (GameFolder / "later.log").WriteAllTextAsync("later");
        await Synchronizer.Synchronize(Refresh(loadout));

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        (GameFolder / "later.log").FileExists.Should().BeFalse("the clean said it would take the file out of the folder");
        var holder = FoundGroups(Refresh(loadout)).Single(group => FilesOf(group).Contains("later.log"));
        holder.IsDisabled.Should().BeTrue("a group that is switched on would keep the file on disk");
    }

    [Fact]
    public async Task ACleanThatFailsAfterTheApplyTookTheFilesOffDiskKeepsThemInTheSwitchedOffGroup()
    {
        var loadout = await LoadoutWithUnknownFiles();
        var locked = GameFolder / "locked";
        locked.CreateDirectory();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"locked/mod.txt"], loadout.LoadoutId, "Needs a locked folder");
            await tx.Commit();
        }

        // The Apply deletes the unknown files first, then fails extracting the mod's file into the folder it cannot write to.
        File.SetUnixFileMode(locked.ToString(), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            File.SetUnixFileMode(locked.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        (GameFolder / "crash.dmp").FileExists.Should().BeFalse("the Apply had already taken the files off disk when it failed");

        await Synchronizer.Synchronize(Refresh(loadout));

        var group = FoundGroup(Refresh(loadout));
        group.IsDisabled.Should().BeTrue();
        FilesOf(group).Should().Equal("Mods/Cool/a.pak", "Mods/Cool/b.pak", "crash.dmp");
    }
}
