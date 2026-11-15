using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Both RunActions overloads refuse an extraction the file store cannot serve before they delete anything, both
/// take a folder away when its last file is deleted, and a reset refuses the actions that need a loadout.
/// </summary>
public class RunActionsRefusalTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<RunActionsRefusalTests>(helper)
{
    private static readonly GamePath Unstored = new(LocationId.Game, "unstored.bin");
    private static readonly GamePath Doomed = new(LocationId.Game, "doomed.bin");
    private static readonly GamePath Nested = new(LocationId.Game, "OnlyFolder/last.bin");

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations[LocationId.Game].Path / path.Path;

    private static SyncNodePart Part(ulong seed) => new() { Identity = FileIdentity.XxHash3(Hash.From(seed)), Size = Size.From(1), LastModifiedTicks = 1 };

    /// <summary>Writes the file and describes it as the index would, so nothing re-reads it and rethinks the actions.</summary>
    private async Task<SyncNodePart> WriteFile(GamePath path)
    {
        OnDisk(path).Parent.CreateDirectory();
        await OnDisk(path).WriteAllTextAsync("x");
        var file = OnDisk(path);
        return new SyncNodePart
        {
            Identity = FileIdentity.XxHash3(await file.XxHash3Async(token: CancellationToken.None)),
            Size = file.FileInfo.Size,
            LastModifiedTicks = file.FileInfo.LastWriteTimeUtc.Ticks,
        };
    }

    private async Task<Dictionary<GamePath, SyncNode>> ExtractThatCannotBeServedPlusADelete()
    {
        var doomed = await WriteFile(Doomed);
        return new Dictionary<GamePath, SyncNode>
        {
            [Unstored] = new() { Loadout = Part(0xB1), Actions = Actions.ExtractToDisk },
            [Doomed] = new() { Disk = doomed, Previous = doomed, Actions = Actions.DeleteFromDisk },
        };
    }

    [Fact]
    public async Task Reset_ExtractionWhoseBytesAreNotStored_IsRefusedBeforeAnythingIsDeleted()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var tree = await ExtractThatCannotBeServedPlusADelete();

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, GameInstallation);

        var refusal = (await act.Should().ThrowAsync<MissingFileBytesException>()).Which;
        refusal.Paths.Should().Equal(Unstored);
        refusal.Title.Should().Be("The game folder was not reset");
        refusal.Message.Should().NotContain("loadout", "there is no loadout to take the file out of in a reset");
        OnDisk(Doomed).FileExists.Should().BeTrue("the refusal comes before the delete phase");
    }

    [Fact]
    public async Task Loadout_ExtractionWhoseBytesAreNotStored_IsRefusedBeforeAnythingIsDeleted()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var tree = await ExtractThatCannotBeServedPlusADelete();

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, loadout);

        var refusal = (await act.Should().ThrowAsync<MissingFileBytesException>()).Which;
        refusal.Paths.Should().Equal(Unstored);
        refusal.Title.Should().Be("The loadout was not applied");
        refusal.Message.Should().Contain("remove it from the loadout");
        OnDisk(Doomed).FileExists.Should().BeTrue("the refusal comes before the delete phase");
    }

    private async Task<Dictionary<GamePath, SyncNode>> DeleteOfTheOnlyFileInAFolder()
    {
        var nested = await WriteFile(Nested);
        return new Dictionary<GamePath, SyncNode>
        {
            [Nested] = new() { Disk = nested, Previous = nested, Actions = Actions.DeleteFromDisk },
        };
    }

    [Fact]
    public async Task Reset_DeletingTheLastFileInAFolder_RemovesTheFolder()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var tree = await DeleteOfTheOnlyFileInAFolder();

        await ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, GameInstallation);

        OnDisk(Nested).FileExists.Should().BeFalse();
        OnDisk(Nested).Parent.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task Loadout_DeletingTheLastFileInAFolder_RemovesTheFolder()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var tree = await DeleteOfTheOnlyFileInAFolder();

        await ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, loadout);

        OnDisk(Nested).FileExists.Should().BeFalse();
        OnDisk(Nested).Parent.DirectoryExists().Should().BeFalse();
    }

    // WriteIntrinsic has its own test in OutsideLoadoutActionTests.
    [Theory]
    [InlineData(Actions.IngestFromDisk, "ingest")]
    [InlineData(Actions.AdaptLoadout, "adapt loadout")]
    [InlineData(Actions.AddReifiedDelete, "reified deletes")]
    public async Task Reset_ALoadoutOnlyAction_IsRefused(Actions action, string messageFragment)
    {
        // The check only runs in debug builds (or with the debug switch set).
        ApplicationConstants.IsDebug.Should().BeTrue();

        await LoadoutManager.ManageInstallation(GameInstallation);

        // Only the one action is set: a check that looks at the wrong flag lets this tree through.
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "loadout-only.bin")] = new() { Disk = Part(0xC1), Actions = action },
        };

        var act = () => ((ALoadoutSynchronizer)Synchronizer).RunActions(tree, GameInstallation);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(messageFragment);
    }
}
