using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A game with a generated file of its own (a plugins list) has that file as a pending write whenever the status
/// cannot show it is unchanged. A file no Apply can write must not keep the button lit through it, neither after an
/// Apply that left the file out nor after the player settled it.
/// </summary>
[Collection(RenderedListingStubbedGame.CollectionName)]
public class UnwritableFilesWithGeneratedFileTests : AIsolatedGameTest<UnwritableFilesWithGeneratedFileTests, RenderedListingStubbedGame>
{
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    public UnwritableFilesWithGeneratedFileTests(ITestOutputHelper helper) : base(helper) => RenderedListingStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<RenderedListingStubbedGame>()
        .AddUniversalGameLocator<RenderedListingStubbedGame>(Version.Parse("0.0.0"))
        .AddSingleton(new BackupLimit(Size.KB * 8))
        .AddSingleton<IBackupOverflowPrompt>(_prompt);

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;
    private static readonly GamePath Ext0 = new(LocationId.Game, "ext0.bin");

    /// <summary>A mod owns ext0.bin and wants bytes the store does not hold, while the file on disk is the one the last apply left.</summary>
    private async Task<Loadout.ReadOnly> ModFileWithNoCopyAndDifferentOnDisk()
    {
        GameDir.CreateDirectory();
        for (var i = 0; i < 4; i++)
        {
            var bytes = new byte[4096];
            Array.Fill(bytes, (byte)(i + 1));
            File.WriteAllBytes((GameDir / $"ext{i}.bin").ToString(), bytes);
        }

        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();

        var row = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Single(f => ((GamePath)f.TargetPath).Path.FileName.ToString() == "ext0.bin");
        using (var tx = Connection.BeginTransaction())
        {
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "Some mod", LoadoutId = loadout.Id },
            };
            tx.Add(row.Id, LoadoutItem.Parent, groupId);
            tx.Add(row.Id, LoadoutFile.Hash, "bytes nobody kept".xxHash3AsUtf8());
            await tx.Commit();
        }
        return Refresh(loadout);
    }

    private async Task<LoadoutSynchronizerState> Status(Loadout.ReadOnly loadout)
        => await (await SynchronizerService.StatusForLoadout(loadout.LoadoutId)).FirstAsync(s => s != LoadoutSynchronizerState.Pending);

    [Fact]
    public async Task AnApplyThatLeftAFileOutLeavesTheStatusCurrentWhenTheGeneratedFileIsAlwaysAWrite()
    {
        // The generated file does not render from the loadout alone, so a comparison would find it pending every time.
        RenderedListingStubbedGame.OptIn = false;
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();
        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);

        await Synchronizer.Synchronize(loadout, job);

        job.UnwritableFiles.Should().HaveCount(1, "the premise: the apply left a file out");
        (await Status(loadout)).Should().Be(LoadoutSynchronizerState.Current);
    }

    // The status counts the file store's changes from the Apply it answers for, not from whenever it last looked,
    // so a second Apply that leaves the same file out (and backs up whatever it backs up) is not a reason to compare.
    [Fact]
    public async Task ASecondApplyThatLeftTheSameFileOutLeavesTheStatusCurrent()
    {
        RenderedListingStubbedGame.OptIn = false;
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();
        await Synchronizer.Synchronize(loadout);
        (await Status(loadout)).Should().Be(LoadoutSynchronizerState.Current, "the premise: the first Apply reads current");

        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);
        await Synchronizer.Synchronize(Refresh(loadout), job);

        job.UnwritableFiles.Should().HaveCount(1, "the premise: the second apply left the same file out");
        (await Status(loadout)).Should().Be(LoadoutSynchronizerState.Current);
    }

    [Fact]
    public async Task SettlingTheFileLeavesTheStatusCurrentWhenTheGeneratedFileRendersTheSame()
    {
        var loadout = await ModFileWithNoCopyAndDifferentOnDisk();
        await Synchronizer.Synchronize(loadout);

        await SynchronizerService.ResolveUnwritableFiles(loadout.LoadoutId, [Ext0], UnwritableResolution.KeepDiskFile);

        (await Status(loadout)).Should().Be(LoadoutSynchronizerState.Current);
    }
}
