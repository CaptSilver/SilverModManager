using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Trees;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A loadout file whose bytes are neither in the file store nor on disk cannot be written by an Apply. Such a file
/// is not pending work: the preview and the status leave it out, the Apply says what it could not write, and the
/// player can settle each file in place by keeping what is on disk or by forgetting the file.
/// </summary>
public class UnwritableFilesTests(ITestOutputHelper helper) : AStubbedGameTest<UnwritableFilesTests>(helper)
{
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddSingleton(new BackupLimit(Size.KB * 8))
            .AddSingleton<IBackupOverflowPrompt>(_prompt);

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;
    private static readonly GamePath Ext0 = new(LocationId.Game, "ext0.bin");

    /// <summary>
    /// A mod owns ext0.bin, whose bytes were adopted in place, so the store holds no copy, and the file is then gone from disk.
    /// </summary>
    private async Task<Loadout.ReadOnly> ModFileWithNoCopyAndNotOnDisk()
    {
        WriteExternalFiles();
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        await MoveIntoAMod(loadout, "ext0.bin");
        (GameDir / "ext0.bin").Delete();
        return Refresh(loadout);
    }

    /// <summary>
    /// A mod owns ext0.bin and wants bytes the store does not hold, while the file on disk is the one the last apply left.
    /// </summary>
    private async Task<Loadout.ReadOnly> ModFileWithNoCopyAndDifferentOnDisk()
    {
        WriteExternalFiles();
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        await MoveIntoAMod(loadout, "ext0.bin");
        var row = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Where(f => (GamePath)f.TargetPath == Ext0).OfTypeLoadoutFile().Single();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(row.Id, LoadoutFile.Hash, "bytes nobody kept".xxHash3AsUtf8());
            await tx.Commit();
        }
        return Refresh(loadout);
    }

    private void WriteExternalFiles()
    {
        GameDir.CreateDirectory();
        for (var i = 0; i < 4; i++)
        {
            var bytes = new byte[4096];
            Array.Fill(bytes, (byte)(i + 1));
            File.WriteAllBytes((GameDir / $"ext{i}.bin").ToString(), bytes);
        }
    }

    private FileChangeType ChangeOf(Loadout.ReadOnly loadout, GamePath path)
        => SynchronizerService.GetApplyDiffTree(loadout.LoadoutId).GetAllDescendentFiles()
            .Single(f => f.GamePath() == path).Item.Value!.ChangeType;

    private async Task<Dictionary<GamePath, SyncNode>> Tree(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        return tree;
    }

    [Fact]
    public async Task FileMissingFromDiskAndStore_IsNotPendingInThePreview()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();
        (await Tree(loadout))[Ext0].Actions.Should().Be(Actions.WarnOfUnableToExtract, "the premise: nothing can be written for it");

        ChangeOf(loadout, Ext0).Should().Be(FileChangeType.Unrestorable, "the preview must not offer to add a file nothing can write");
    }

    [Fact]
    public async Task FileMissingFromDiskAndStore_DoesNotMakeTheLoadoutNeedAnApply()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();
        var tree = await Tree(loadout);
        tree[Ext0].Actions.Should().Be(Actions.WarnOfUnableToExtract);

        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        var evaluation = await Synchronizer.EvaluateSynchronize(loadout, Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));

        evaluation.Needed.Should().BeFalse("a file with no bytes anywhere is not work an Apply can do");
        evaluation.WaitsForArchive.Should().BeTrue("the status still looks again when an archive returns");
    }

    [Fact]
    public async Task FileMissingFromDiskAndStore_LeavesTheStatusCurrent()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();
        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var status = await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId)).FirstAsync(s => s != LoadoutSynchronizerState.Pending);

        status.Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task AnApplyReportsTheFilesItCouldNotWrite()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();
        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);

        await Synchronizer.Synchronize(loadout, job);

        job.UnwritableFiles.Should().Equal(new UnwritableFile(Ext0, UnwritableReason.NotOnDisk));
        SynchronizerService.GetUnwritableFiles(loadout.LoadoutId).Should().Equal(new UnwritableFile(Ext0, UnwritableReason.NotOnDisk));
        (GameDir / "ext0.bin").FileExists.Should().BeFalse("nothing can be written for it");
    }

    [Fact]
    public async Task AnApplyThatWritesEverythingReportsNothing()
    {
        WriteExternalFiles();
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);

        await Synchronizer.Synchronize(Refresh(loadout), job);

        job.UnwritableFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task FileOnDiskWithOtherBytesThanTheLoadoutWants_IsUnrestorableNotPending()
    {
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();
        (await Tree(loadout))[Ext0].Actions.Should().Be(Actions.WarnOfUnableToExtract, "the premise: the loadout wants bytes nobody kept");

        ChangeOf(loadout, Ext0).Should().Be(FileChangeType.Unrestorable);
        SynchronizerService.GetUnwritableFiles(loadout.LoadoutId).Should().Equal(new UnwritableFile(Ext0, UnwritableReason.DifferentOnDisk));
    }

    [Fact]
    public async Task KeepingTheDiskFile_BacksItUpAndEndsThePendingApply()
    {
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();
        var onDisk = await Xx3Hasher.HashAsync((GameDir / "ext0.bin").Read());

        var result = await SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.KeepDiskFile);

        result.Resolved.Should().Equal(Ext0);
        (await FileStore.HaveFile(FileIdentity.XxHash3(onDisk))).Should().BeTrue("the accepted file is backed up");
        SynchronizerService.GetUnwritableFiles(loadout.LoadoutId).Should().BeEmpty();
        (await Tree(loadout))[Ext0].Actions.Should().Be(Actions.DoNothing);
        ChangeOf(loadout, Ext0).Should().Be(FileChangeType.None);
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        (await Synchronizer.EvaluateSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata)))
            .Should().Be(new SynchronizeEvaluation(false, false));

        await Synchronizer.Synchronize(Refresh(loadout));
        (await Xx3Hasher.HashAsync((GameDir / "ext0.bin").Read())).Should().Be(onDisk, "the file the player kept is still the one on disk");
    }

    [Fact]
    public async Task ForgettingAFileThatIsNotOnDisk_EndsThePendingApplyAndTheFileStaysGone()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();

        var result = await SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.Forget);

        result.Resolved.Should().Equal(Ext0);
        SynchronizerService.GetUnwritableFiles(loadout.LoadoutId).Should().BeEmpty();
        (await Tree(loadout))[Ext0].Actions.Should().Be(Actions.DoNothing);
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        (await Synchronizer.EvaluateSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata)))
            .Should().Be(new SynchronizeEvaluation(false, false));

        await Synchronizer.Synchronize(Refresh(loadout));
        (GameDir / "ext0.bin").FileExists.Should().BeFalse("forgetting a file does not bring it back");
    }

    [Fact]
    public async Task ResolvingWithoutPaths_SettlesEveryFileTheResolutionFits()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();

        var result = await SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, null, UnwritableResolution.Forget);

        result.Resolved.Should().Equal(Ext0);
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task KeepingTheDiskFileOfAFileThatIsNotOnDisk_IsRefusedAndChangesNothing()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();

        var refusal = await Assert.ThrowsAsync<UnwritableResolutionRefusedException>(
            () => SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.KeepDiskFile));

        refusal.Message.Should().Contain("forget");
        SynchronizerService.GetUnwritableFiles(loadout.LoadoutId).Should().HaveCount(1);
    }

    [Fact]
    public async Task ForgettingAFileThatIsOnDisk_IsRefusedBecauseTheNextApplyWouldDeleteIt()
    {
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();

        var refusal = await Assert.ThrowsAsync<UnwritableResolutionRefusedException>(
            () => SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.Forget));

        refusal.Message.Should().Contain("delete");
        (GameDir / "ext0.bin").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task ResolvingAPathThatIsNotUnwritable_IsRefused()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();

        await Assert.ThrowsAsync<UnwritableResolutionRefusedException>(
            () => SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [new GamePath(LocationId.Game, "ext1.bin")], UnwritableResolution.Forget));
    }

    [Fact]
    public async Task ResolvingLeavesAnEditToAnotherFileForTheNextApplyToFind()
    {
        var loadout = await ModFileWithNoCopyAndNotOnDisk();
        File.WriteAllBytes((GameDir / "ext1.bin").ToString(), new byte[4096]);

        await SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.Forget);

        var tree = await Tree(loadout);
        tree[new GamePath(LocationId.Game, "ext1.bin")].Actions.Should().HaveFlag(Actions.IngestFromDisk, "the edit was not swallowed by settling another file");
    }
}
