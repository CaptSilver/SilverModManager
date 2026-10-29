using System.Collections.Frozen;
using FluentAssertions;
using SilverModManager.Backend.Games;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using R3;
using SilverModManager.DataModel;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Added files over the backup cap are put to the player instead of failing the sync. The cap is 8 KB here, so
/// four 4 KB files are over it and one 1 KB file is not.
/// </summary>
public class BackupOverflowTests(ITestOutputHelper helper) : AStubbedGameTest<BackupOverflowTests>(helper)
{
    private readonly SwitchableBackupOverflowPrompt _prompt = new();

    private bool _indexingFails;
    private bool _readingFails;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddSingleton(new BackupLimit(Size.KB * 8))
            .AddSingleton<IBackupOverflowPrompt>(_prompt)
            .AddSingleton<IFullFileHasher>(new SwitchableFileHasher(() => _readingFails));

        // The real indexer, behind a switch that makes it report a failing disk.
        var real = services.Last(d => d.ServiceType == typeof(IGameLocationsService));
        services.Remove(real);
        return services.AddSingleton<IGameLocationsService>(sp =>
            new SwitchableIndexer((IGameLocationsService)ActivatorUtilities.CreateInstance(sp, real.ImplementationType ?? throw new InvalidOperationException()), () => _indexingFails));
    }

    /// <summary>The real hasher, behind a switch that makes a file read as one that changed under it.</summary>
    private sealed class SwitchableFileHasher(Func<bool> fails) : IFullFileHasher
    {
        public ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
            => fails() ? throw new GameFileChangedWhileReadException(file) : MultiHashFileHasher.Instance.Hash(file, cancellationToken);

        public ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
            => fails() ? throw new GameFileChangedWhileReadException(file) : MultiHashFileHasher.Instance.HashAll(file, cancellationToken);
    }

    private sealed class SwitchableIndexer(IGameLocationsService inner, Func<bool> fails) : IGameLocationsService
    {
        public Task<IndexGameResult> IndexGame(GameInstallation installation, FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousDiskState, IGamePathFilter filter,
            CancellationToken cancellationToken = default, Action<long, long>? progress = null, Func<IReadOnlyList<IndexFileResult>, ValueTask>? onBatch = null)
            => fails()
                ? throw new GameFolderUnreadableException(installation, "Listing the files", new IOException("Input/output error"))
                : inner.IndexGame(installation, previousDiskState, filter, cancellationToken, progress, onBatch);
    }

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private static byte[] BytesOf(string name, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)name.Sum(c => c));
        return bytes;
    }

    private void WriteExternalFile(string name, int length)
    {
        var path = GameDir / name;
        path.Parent.CreateDirectory();
        File.WriteAllBytes(path.ToString(), BytesOf(name, length));
    }

    private void WriteExternalFiles(int count, int eachBytes)
    {
        for (var i = 0; i < count; i++) WriteExternalFile($"ext{i}.bin", eachBytes);
    }

    private static Hash HashOf(string name, int length = 4096) => BytesOf(name, length).xxHash3();

    private bool OnDisk(string name) => (GameDir / name).FileExists;

    private IEnumerable<string> ExternalChanges(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(f => ((GamePath)f.TargetPath).Path.FileName.ToString())
            .Where(n => n.StartsWith("ext") || n.StartsWith("late"));

    private int VisibleLoadouts => Loadout.All(Connection.Db).Count(l => l.IsVisible());

    [Fact]
    public async Task FirstSync_OverTheCap_AsksThePromptBeforeAnythingIsWritten()
    {
        WriteExternalFiles(count: 4, eachBytes: 4096);
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        await CreateLoadout();

        var asked = prompt.Asked.Should().ContainSingle().Which;
        asked.AdoptableCount.Should().Be(4);
        asked.Adoptable.Should().Be(Size.KB * 16);
        asked.MustBackUpCount.Should().Be(0);
        asked.CanAdopt.Should().BeTrue();
        asked.Operation.Should().Be(BackupOverflowOperation.Manage);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeTrue();
    }

    [Fact]
    public async Task AdoptInPlace_IngestsWithoutArchiving()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);

        var loadout = await CreateLoadout();

        ExternalChanges(loadout).Should().HaveCount(4);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
        OnDisk("ext0.bin").Should().BeTrue();
    }

    [Fact]
    public async Task UnderTheCap_NeverAsks()
    {
        WriteExternalFile("ext0.bin", 1024);
        var prompt = new RecordingPrompt(BackupOverflowChoice.Refuse);
        _prompt.Inner = prompt;

        await CreateLoadout();

        prompt.Asked.Should().BeEmpty();
        (await FileStore.HaveFile(HashOf("ext0.bin", 1024))).Should().BeTrue();
    }

    [Fact]
    public async Task PresetChoice_SkipsThePrompt()
    {
        WriteExternalFiles(4, 4096);
        var prompt = new RecordingPrompt(BackupOverflowChoice.Refuse);
        _prompt.Inner = prompt;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, largeFiles: BackupOverflowChoice.AdoptInPlace);

        prompt.Asked.Should().BeEmpty();
        ExternalChanges(loadout).Should().HaveCount(4);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
    }

    [Fact]
    public async Task RefusePreset_ThrowsAReadableRefusalWithoutAskingAndWritesNothing()
    {
        WriteExternalFiles(4, 4096);
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        var act = async () => await LoadoutManager.CreateLoadout(GameInstallation, largeFiles: BackupOverflowChoice.Refuse);

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.CommandLineHint.Should().Contain("--large-files adopt").And.Contain("--large-files backup").And.Contain("loadout create");
        refusal.Message.Should().Contain("16 KB").And.Contain("not your mods").And.NotContain("`");
        prompt.Asked.Should().BeEmpty();
        VisibleLoadouts.Should().Be(0);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
    }

    [Fact]
    public async Task RefusedApply_NamesTheSynchronizeVerbNotTheCreateVerb()
    {
        WriteExternalFiles(4, 4096);
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, largeFiles: BackupOverflowChoice.AdoptInPlace);
        WriteExternalFile("late0.bin", 4096);
        WriteExternalFile("late1.bin", 4096);
        WriteExternalFile("late2.bin", 4096);

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId, BackupOverflowChoice.Refuse);

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.CommandLineHint.Should().Contain("loadout synchronize").And.NotContain("loadout create");
        refusal.Message.Should().NotContain("`").And.NotContain("silvermod");
    }

    [Theory]
    [InlineData(BackupOverflowOperation.Manage, "Files were not backed up", "loadout create --game", "--large-files adopt", "keep them where they are")]
    [InlineData(BackupOverflowOperation.Apply, "Files were not backed up", "loadout synchronize", "--large-files adopt", "leave them where they are")]
    [InlineData(BackupOverflowOperation.Launch, "The game was not started", "loadout synchronize", "launch again", "then launch again")]
    [InlineData(BackupOverflowOperation.Switch, "Files were not backed up", "backup-game-files", "try again", "try again")]
    [InlineData(BackupOverflowOperation.Reset, "Files were not backed up", "backup-game-files", "try again", "try again")]
    public void RefusalWording_FollowsTheOperation(BackupOverflowOperation operation, string title, string cliHint, string tail, string tailPhrase)
    {
        var overflow = new BackupOverflow(Size.KB * 16, 4, Size.Zero, 0, Size.KB * 8, operation);

        var refusal = new BackupOverflowException(GameInstallation, overflow);

        refusal.Title.Should().Be(title);
        refusal.CommandLineHint.Should().Contain(cliHint).And.Contain(tail).And.Contain(tailPhrase);
        if (operation is BackupOverflowOperation.Switch or BackupOverflowOperation.Reset) refusal.CommandLineHint.Should().NotContain("--large-files");
    }

    [Fact]
    public void RefusalMessage_HasNoCommandLine()
    {
        var overflow = new BackupOverflow(Size.KB * 16, 4, Size.Zero, 0, Size.KB * 8, BackupOverflowOperation.Apply);

        var message = new BackupOverflowException(GameInstallation, overflow).Message;

        message.Should().NotContain("`").And.NotContain("silvermod").And.NotContain("--large-files").And.NotContain("added files");
        message.Should().Contain("nothing was changed");
    }

    [Fact]
    public void AdoptWouldLoseFiles_SaysItInPlayerWordsAndKeepsTheFlagInTheHint()
    {
        var overflow = new BackupOverflow(Size.KB * 16, 4, Size.KB * 12, 3, Size.KB * 8, BackupOverflowOperation.Switch);

        var refusal = BackupOverflowException.AdoptWouldLoseFiles(GameInstallation, overflow);

        refusal.Message.Should().Contain("3 files (12 KB)").And.Contain("keeping them where they are would lose them").And.NotContain("--large-files").And.NotContain("adopt");
        refusal.CommandLineHint.Should().Be("Use --large-files backup.");
    }

    [Fact]
    public void RefusalAndLogSizes_UseTheSameUnitsAsTheDialog()
    {
        var overflow = new BackupOverflow(Size.From(15_748_000_000), 4506, Size.Zero, 0, Size.GB * 5);

        var message = new BackupOverflowException(GameInstallation, overflow).Message;

        message.Should().Contain("14.7 GB").And.Contain("5.0 GB").And.NotContain("14.666");
        overflow.TotalText.Should().Be("14.7 GB");
    }

    [Fact]
    public async Task PromptAnsweringRefuse_IsTheSameRefusal()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.Refuse);

        var act = async () => await CreateLoadout();

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.Declined.Should().BeTrue();
        refusal.CommandLineHint.Should().Contain("--large-files adopt");
        refusal.Message.Should().Contain("16 KB");
        VisibleLoadouts.Should().Be(0);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
    }

    [Fact]
    public async Task AdoptedFiles_AreCountedAsUnbacked()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();

        var unbacked = await LoadoutManager.CountUnbackedFiles(GameInstallation);

        unbacked.Should().Be(new UnbackedFiles(4, Size.KB * 16));
    }

    [Fact]
    public async Task BackedUpFiles_AreNotCountedAsUnbacked()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        await CreateLoadout();

        (await LoadoutManager.CountUnbackedFiles(GameInstallation)).Should().Be(default(UnbackedFiles));
    }

    [Fact]
    public async Task UnbackedFilesWithTheSameContent_AreCountedOnce()
    {
        WriteExternalFile("aa.bin", 4096);
        File.WriteAllBytes((GameDir / "copy.bin").ToString(), BytesOf("aa.bin", 4096));
        WriteExternalFiles(3, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();

        var unbacked = await LoadoutManager.CountUnbackedFiles(GameInstallation);

        unbacked.Count.Should().Be(4, "aa.bin and copy.bin are one blob, plus ext0-2");
    }

    [Fact]
    public async Task SyncStartedWhileThePromptIsOpen_WaitsForTheAnswer()
    {
        WriteExternalFiles(4, 4096);
        Task? concurrent = null;
        var questions = 0;
        var questionsWhileOpen = -1;
        _prompt.Inner = new DelegatePrompt(async (_, _, token) =>
        {
            // Only the first question starts the concurrent sync. An unguarded one would fail with "State is
            // already locked" rather than put a second question, so the checks below are on the count while the
            // question is open and on the concurrent sync finishing cleanly.
            if (Interlocked.Increment(ref questions) > 1) return BackupOverflowChoice.BackUpAnyway;

            var existing = Loadout.All(Connection.Db).First(l => l.IsVisible());
            concurrent = SynchronizerService.Synchronize(existing.LoadoutId);
            await Task.Delay(500, token);
            questionsWhileOpen = Volatile.Read(ref questions);
            return BackupOverflowChoice.BackUpAnyway;
        });

        await CreateLoadout();

        concurrent.Should().NotBeNull();
        questionsWhileOpen.Should().Be(1, "the synchronizer service is held until the question is answered");
        await concurrent!.WaitAsync(TimeSpan.FromSeconds(30));
        questions.Should().Be(1, "once answered, the files are backed up and nothing is over the cap any more");
    }

    [Fact]
    public async Task FileAddedWhileThePromptIsOpen_IsInTheTreeThatRuns()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new DelegatePrompt((_, _, _) =>
        {
            WriteExternalFile("late.bin", 100);
            return ValueTask.FromResult(BackupOverflowChoice.BackUpAnyway);
        });

        var loadout = await CreateLoadout();

        ExternalChanges(loadout).Should().Contain("late.bin");
        (await FileStore.HaveFile(HashOf("late.bin", 100))).Should().BeTrue();
    }

    [Fact]
    public async Task CancellingTheJobWhileThePromptIsOpen_EndsTheWaitAndLeavesNoLoadout()
    {
        WriteExternalFiles(4, 4096);
        var asking = new TaskCompletionSource();
        _prompt.Inner = new DelegatePrompt(async (_, _, token) =>
        {
            asking.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        });

        var job = LoadoutManager.CreateLoadout(GameInstallation);
        await asking.Task.WaitAsync(TimeSpan.FromSeconds(30));
        ServiceProvider.GetRequiredService<IJobMonitor>().Cancel(job);

        var wait = async () => await job;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        VisibleLoadouts.Should().Be(0);
    }

    [Fact]
    public async Task UnmanageAfterAdopt_AsksNothingAndArchivesNothingBecauseTheCopyWouldBeDiscarded()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        // No garbage collection: any copy made on the way would still be in the store afterwards.
        await SynchronizerService.UnManage(GameInstallation, runGc: false, cleanGameFolder: true);

        prompt.Asked.Should().BeEmpty("unmanaging lets go of every copy, so none is worth asking about");
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
        OnDisk("ext0.bin").Should().BeFalse();
    }

    [Fact]
    public async Task UnmanageAfterAdopt_WithAnAdoptPreset_StillCleansTheFolder()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();

        await SynchronizerService.UnManage(GameInstallation, cleanGameFolder: true, largeFiles: BackupOverflowChoice.AdoptInPlace);

        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeFalse();
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AdoptPresetWhereFilesWouldBeDeleted_IsRefusedReadably()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout), BackupOverflowChoice.AdoptInPlace);

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.Message.Should().Contain("deleted or replaced").And.NotContain("--large-files");
        refusal.CommandLineHint.Should().Contain("--large-files backup");
        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeTrue();
        ExternalChanges(Refresh(loadout)).Should().HaveCount(4, "a refused clean leaves the files as External Changes");
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).HasValue.Should().BeTrue("the install stays managed");
    }

    [Fact]
    public async Task CleaningAfterAdopt_AndDecliningTheQuestion_KeepsTheExternalChangesAndDeletesNothing()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.Refuse);

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        (await act.Should().ThrowAsync<BackupOverflowException>()).Which.Declined.Should().BeTrue();
        ExternalChanges(Refresh(loadout)).Should().HaveCount(4);
        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeTrue();
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
    }

    [Fact]
    public async Task CleaningAfterAdopt_WhenTheDiskFailsBeforeTheApplyWrites_KeepsTheExternalChanges()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        _indexingFails = true;

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        await act.Should().ThrowAsync<GameFolderUnreadableException>();
        ExternalChanges(Refresh(loadout)).Should().HaveCount(4, "the clean never got to change the folder");
        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeTrue();
    }

    [Fact]
    public async Task CleaningAfterAdopt_WhenAFileChangesWhileItIsReadForTheBackup_KeepsTheExternalChanges()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _readingFails = true;

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        await act.Should().ThrowAsync<GameFileChangedWhileReadException>();
        ExternalChanges(Refresh(loadout)).Should().HaveCount(4, "the read stopped the apply before it archived or deleted anything");
        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeTrue();
    }

    [Fact]
    public async Task CleaningAfterAdopt_AndBackingUp_DeletesTheExternalChangesAfterTheCopiesExist()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        prompt.Asked.Should().ContainSingle().Which.Operation.Should().Be(BackupOverflowOperation.Reset);
        ExternalChanges(Refresh(loadout)).Should().BeEmpty();
        OnDisk("ext0.bin").Should().BeFalse();
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeTrue();
    }

    [Fact]
    public async Task SyncStartedWhileTheCleanQuestionIsOpen_WaitsForTheAnswer()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var loadout = await CreateLoadout();
        Task? concurrent = null;
        var completedWhileOpen = true;
        _prompt.Inner = new DelegatePrompt(async (_, _, token) =>
        {
            concurrent = SynchronizerService.Synchronize(loadout.LoadoutId, BackupOverflowChoice.BackUpAnyway);
            await Task.Delay(500, token);
            completedWhileOpen = concurrent.IsCompleted;
            return BackupOverflowChoice.BackUpAnyway;
        });

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        concurrent.Should().NotBeNull();
        completedWhileOpen.Should().BeFalse("the clean holds the synchronizer service until the question is answered");
        await concurrent!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task LoadoutSwitchOverTheCap_AsksOnce()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var first = await CreateLoadout();
        var second = await LoadoutManager.CopyLoadout(first.LoadoutId);
        for (var i = 0; i < 4; i++) WriteExternalFile($"late{i}.bin", 4096);
        var asked = new List<BackupOverflow>();
        // Answers as the dialog does: adopt where it is on offer, back up where it is not.
        _prompt.Inner = new DelegatePrompt((_, overflow, _) =>
        {
            asked.Add(overflow);
            return ValueTask.FromResult(overflow.CanAdopt ? BackupOverflowChoice.AdoptInPlace : BackupOverflowChoice.BackUpAnyway);
        });

        await SynchronizerService.Synchronize(second.LoadoutId);

        asked.Should().ContainSingle("one answer covers the deactivate's sync, the reset and the activation").Which.Operation.Should().Be(BackupOverflowOperation.Apply);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(second.LoadoutId);
    }

    [Fact]
    public async Task LoadoutSwitchWithAnAdoptPreset_Completes()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var first = await CreateLoadout();
        var second = await LoadoutManager.CopyLoadout(first.LoadoutId);

        await SynchronizerService.Synchronize(second.LoadoutId, BackupOverflowChoice.AdoptInPlace);

        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(second.LoadoutId, "the reset's copies can only be made, so the answer stands for them");
    }

    // The folder changed while the dialog was open: a file that has to be backed up joined the ones that only wanted a copy.
    private static Dictionary<GamePath, SyncNode> TreeWhereAdoptingLosesFiles()
    {
        SyncNode Node(ulong hash, Actions actions) => new()
        {
            Disk = new SyncNodePart { Identity = FileIdentity.XxHash3(Hash.From(hash)), Size = Size.KB * 6, LastModifiedTicks = 1 },
            Actions = actions,
        };

        return new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "wants-a-copy.bin")] = Node(0xA1, Actions.BackupFile | Actions.IngestFromDisk),
            [new GamePath(LocationId.Game, "replaced.bin")] = Node(0xB2, Actions.BackupFile | Actions.ExtractToDisk),
            [new GamePath(LocationId.Game, "deleted.bin")] = Node(0xC3, Actions.BackupFile | Actions.DeleteFromDisk),
        };
    }

    [Fact]
    public async Task AnAdoptAnswerThatNoLongerApplies_IsAskedAgainInsteadOfFailing()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        var (choice, asked) = await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(
            TreeWhereAdoptingLosesFiles(), GameInstallation, BackupOverflowChoice.AdoptInPlace, CancellationToken.None, presetIsAnAnswer: true);

        choice.Should().Be(BackupOverflowChoice.BackUpAnyway);
        asked.Should().BeTrue();
        prompt.Asked.Should().ContainSingle().Which.CanAdopt.Should().BeFalse();
    }

    [Fact]
    public async Task AnAdoptPresetTheCallerGave_StillFailsWhenAdoptingLosesFiles()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        var act = () => ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(
            TreeWhereAdoptingLosesFiles(), GameInstallation, BackupOverflowChoice.AdoptInPlace, CancellationToken.None);

        await act.Should().ThrowAsync<BackupOverflowException>();
        prompt.Asked.Should().BeEmpty("an explicit answer from the command line is never second-guessed with a dialog");
    }

    private static SyncNode MeasuredNode(ulong hash, Actions actions, ulong kb = 6, bool loadout = false, bool previous = false, bool gameFile = false)
    {
        SyncNodePart Part() => new() { Identity = FileIdentity.XxHash3(Hash.From(hash)), Size = Size.KB * kb, LastModifiedTicks = 1 };
        return new SyncNode
        {
            Disk = Part(),
            Loadout = loadout ? Part() : new SyncNodePart { Identity = FileIdentity.None, Size = Size.Zero, LastModifiedTicks = 0 },
            Previous = previous ? Part() : new SyncNodePart { Identity = FileIdentity.None, Size = Size.Zero, LastModifiedTicks = 0 },
            Signature = gameFile ? Signature.IsGameFile : default,
            Actions = actions,
        };
    }

    [Fact]
    public async Task OverflowMeasure_GroupsByFolderAndKind()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "content/content0/bundles/a.bundle")] = MeasuredNode(0xA1, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
            [new GamePath(LocationId.Game, "content/content0/b.bundle")] = MeasuredNode(0xA2, Actions.BackupFile | Actions.IngestFromDisk, loadout: true, gameFile: true),
            [new GamePath(LocationId.Game, "mods/m/y.dll")] = MeasuredNode(0xA3, Actions.BackupFile | Actions.IngestFromDisk, previous: true),
            [new GamePath(LocationId.Game, "replaced.bin")] = MeasuredNode(0xA4, Actions.BackupFile | Actions.ExtractToDisk, kb: 1),
        };

        await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(tree, GameInstallation, preset: null, CancellationToken.None, operation: BackupOverflowOperation.Launch);

        var asked = prompt.Asked.Should().ContainSingle().Which;
        asked.Operation.Should().Be(BackupOverflowOperation.Launch);
        asked.Groups.Should().BeEquivalentTo(new[]
        {
            new BackupOverflowGroup("content/content0", 1, Size.KB * 9, BackupOverflowKind.Appeared),
            new BackupOverflowGroup("content/content0", 1, Size.KB * 6, BackupOverflowKind.GameFileChanged),
            new BackupOverflowGroup("mods/m", 1, Size.KB * 6, BackupOverflowKind.ModFileChanged),
            new BackupOverflowGroup(BackupOverflow.GameFolderName, 1, Size.KB * 1, BackupOverflowKind.MustBackUp),
        });
        asked.Examples!.Select(e => e.Path).Should().Equal("content/content0/bundles/a.bundle", "content/content0/b.bundle", "mods/m/y.dll");
        asked.Kinds[BackupOverflowKind.Appeared].Should().Be((1, Size.KB * 9));
        asked.Kinds.Keys.OrderBy(k => k).Should().Equal(BackupOverflowKind.Appeared, BackupOverflowKind.GameFileChanged, BackupOverflowKind.ModFileChanged, BackupOverflowKind.MustBackUp);
    }

    private GameFileRecord KnownGameFile() => ServiceProvider.GetRequiredService<IFileHashesService>().GetGameFiles(GameInstallation).First(f => f.Size.Value > 0);

    private static ALoadoutSynchronizer.MeasuredFile Appeared(GamePath path, ulong size, FileIdentity identity)
        => new(path, size, BackupOverflowKind.Appeared, identity);

    private BackupOverflowKind[] Classify(params ALoadoutSynchronizer.MeasuredFile[] files)
        => ((ALoadoutSynchronizer)Synchronizer).TellGameFilesApart(files, GameInstallation).Select(f => f.Kind).ToArray();

    private static SyncNode DiskOnlyNode(FileIdentity identity, Size size)
    {
        var part = new SyncNodePart { Identity = identity, Size = size, LastModifiedTicks = 1 };
        var none = new SyncNodePart { Identity = FileIdentity.None, Size = Size.Zero, LastModifiedTicks = 0 };
        return new SyncNode { Disk = part, Previous = none, Loadout = none, Actions = Actions.BackupFile | Actions.IngestFromDisk };
    }

    private async Task<BackupOverflow> Measure(Dictionary<GamePath, SyncNode> tree)
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;
        await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(tree, GameInstallation, preset: null, CancellationToken.None, operation: BackupOverflowOperation.Launch);
        return prompt.Asked.Should().ContainSingle().Which;
    }

    [Fact]
    public void DiskOnlyFileOfTheInstalledBuildsList_IsAGameFileThatChanged()
    {
        var known = KnownGameFile();

        Classify(Appeared(known.Path, known.Size.Value, known.Identity)).Should().Equal(BackupOverflowKind.GameFileChanged);
    }

    [Fact]
    public void ListedPathOfAnotherCase_IsStillTheSameGameFile()
    {
        var known = KnownGameFile();
        var upper = new GamePath(known.Path.LocationId, known.Path.Path.ToString().ToUpperInvariant());

        Classify(Appeared(upper, known.Size.Value, known.Identity)).Should().Equal(BackupOverflowKind.GameFileChanged);
    }

    [Fact]
    public void DiskOnlyFileWithAListedPathButAnotherSize_StillAppeared()
    {
        var known = KnownGameFile();

        Classify(Appeared(known.Path, known.Size.Value + 9, known.Identity)).Should().Equal(BackupOverflowKind.Appeared);
    }

    [Fact]
    public void DiskOnlyFileWithAListedPathAndSizeButAnotherHash_StillAppeared()
    {
        var known = KnownGameFile();

        Classify(Appeared(known.Path, known.Size.Value, FileIdentity.XxHash3(Hash.From(0xC2)))).Should().Equal(BackupOverflowKind.Appeared);
    }

    [Fact]
    public void DiskOnlyFileTheListDoesNotHave_StillAppeared()
        => Classify(Appeared(new GamePath(LocationId.Game, "somebody/else.bin"), 9, FileIdentity.XxHash3(Hash.From(0xC3)))).Should().Equal(BackupOverflowKind.Appeared);

    [Fact]
    public void OtherKindsAndLocations_AreLeftAlone()
    {
        var known = KnownGameFile();
        var saves = Appeared(new GamePath(LocationId.Saves, known.Path.Path.ToString()), known.Size.Value, known.Identity);
        var modFile = Appeared(known.Path, known.Size.Value, known.Identity) with { Kind = BackupOverflowKind.ModFileChanged };

        Classify(saves, modFile).Should().Equal(BackupOverflowKind.Appeared, BackupOverflowKind.ModFileChanged);
    }

    [Fact]
    public async Task OverflowMeasure_FilesOutsideTheGameFolder_AreNamedForThePlayer()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.AppDataRoaming, "Foo/bar/x.bin")] = MeasuredNode(0xD1, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
            [new GamePath(LocationId.Saves, "slot1/a.sav")] = MeasuredNode(0xD2, Actions.BackupFile | Actions.IngestFromDisk, kb: 6),
        };

        var asked = await Measure(tree);

        asked.Groups!.Select(g => g.Folder).Should().BeEquivalentTo("AppData/Foo/bar", "Saves/slot1");
        asked.Groups!.Select(g => g.Location).Should().BeEquivalentTo("AppData", "Saves");
        asked.Examples!.Select(e => e.Path).Should().Contain("AppData/Foo/bar/x.bin");
    }

    [Fact]
    public async Task OverflowMeasure_GameSpecificLocation_UsesItsRegisteredNameNotItsId()
    {
        var documents = LocationId.From(StubbedGame.NamedLocationId);
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(documents, "Foo/x.ini")] = MeasuredNode(0xD3, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Folder.Should().Be("Documents/Foo");
        asked.Groups!.Single().Location.Should().Be("Documents");
        asked.Examples!.Single().Path.Should().Be("Documents/Foo/x.ini");
    }

    [Fact]
    public async Task OverflowMeasure_GameLocationWithAPhrase_ReadsItsPhraseInTheSentenceAndItsLabelAsARow()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.From(StubbedGame.PhrasedLocationId), "Foo/x.ini")] = MeasuredNode(0xD8, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Folder.Should().Be("Mod folder/Foo");
        asked.Groups!.Single().Location.Should().Be("the mod folder");
    }

    [Fact]
    public async Task OverflowMeasure_LocationNobodyNamed_ReadsAsOtherFolders()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.From("SyncTestsNobodyNamedThis"), "Foo/x.ini")] = MeasuredNode(0xD4, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Folder.Should().Be("Other folders/Foo");
        asked.Groups!.Single().Location.Should().Be("other folders");
    }

    [Fact]
    public async Task OverflowMeasure_UnknownLocation_ReadsAsOtherFolders()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Unknown, "Foo/x.ini")] = MeasuredNode(0xD5, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Location.Should().Be("other folders");
    }

    [Fact]
    public async Task OverflowMeasure_ModLoaderFolder_ReadsAsAPhraseAfterInAndTo()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Me3Host, "bin/x.dll")] = MeasuredNode(0xD7, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Folder.Should().Be("Mod loader folder/bin");
        asked.Groups!.Single().Location.Should().Be("the mod loader folder");
    }

    [Fact]
    public async Task OverflowMeasure_FilesDirectlyInTheGameFolder_UseTheGameFolderLabel()
    {
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "x.bin")] = MeasuredNode(0xD6, Actions.BackupFile | Actions.IngestFromDisk, kb: 9),
        };

        var asked = await Measure(tree);

        asked.Groups!.Single().Folder.Should().Be(LocationId.Game.DisplayName);
    }

    [Fact]
    public void EndingALeg_PutsTheOperationTheScopeStartedWithBack()
    {
        using var play = BackupOverflowAnswer.Begin(BackupOverflowOperation.Launch);

        using (BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay))
            BackupOverflowAnswer.Active!.Operation.Should().Be(BackupOverflowOperation.AfterPlay);

        BackupOverflowAnswer.Active!.Operation.Should().Be(BackupOverflowOperation.Launch);
    }

    /// <summary>A run-game tool whose run is one more sync, the way the launch session syncs before it starts the process.</summary>
    private sealed class SyncingRunTool(GameId game, ISynchronizerService synchronizer, LoadoutId loadoutId) : IRunGameTool
    {
        public IEnumerable<GameId> GameIds => [game];
        public string Name => "Syncing run";
        public System.IObservable<bool> WaitingForSteam { get; } = new System.Reactive.Subjects.BehaviorSubject<bool>(false);
        public System.IObservable<bool> RunningInstallStep { get; } = new System.Reactive.Subjects.BehaviorSubject<bool>(false);
        public ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
            => monitor.Begin<ITool, Unit>(this, async _ =>
            {
                await synchronizer.Synchronize(loadoutId, cancellationToken: cancellationToken);
                return Unit.Default;
            });
    }

    [Fact]
    public async Task PlayOverTheCap_AsksOnceWithLaunchWording()
    {
        var loadout = await CreateLoadout();
        WriteExternalFiles(count: 4, eachBytes: 4096);
        var prompt = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        _prompt.Inner = prompt;
        var tool = new SyncingRunTool(loadout.Game.GameId, SynchronizerService, loadout.LoadoutId);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, [tool], [], SynchronizerService);

        await toolManager.RunTool(tool, loadout, ServiceProvider.GetRequiredService<IJobMonitor>());

        var asked = prompt.Asked.Should().ContainSingle("the apply, the tool's own sync and the ingest after it share one answer").Which;
        asked.Operation.Should().Be(BackupOverflowOperation.Launch);
    }

    [Fact]
    public async Task AfterPlayIngest_KeepsAppearedFilesWithoutAsking()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.Refuse);
        _prompt.Inner = prompt;
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "save/a.bin")] = MeasuredNode(0xB1, Actions.BackupFile | Actions.IngestFromDisk, kb: 6),
            [new GamePath(LocationId.Game, "save/b.bin")] = MeasuredNode(0xB2, Actions.BackupFile | Actions.IngestFromDisk, kb: 6),
        };

        using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
        var (choice, asked) = await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(tree, GameInstallation, preset: null, CancellationToken.None);

        choice.Should().Be(BackupOverflowChoice.AdoptInPlace);
        asked.Should().BeFalse();
        prompt.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterPlayIngest_AsksWhenKeepingWouldLoseFiles()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
        await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(TreeWhereAdoptingLosesFiles(), GameInstallation, preset: null, CancellationToken.None);

        prompt.Asked.Should().ContainSingle().Which.Operation.Should().Be(BackupOverflowOperation.AfterPlay);
    }

    [Fact]
    public async Task AfterPlayIngest_AfterAPlayThatAskedNothing_UsesAfterPlayWording()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        // The Play opened its scope as a launch; the leg after the game moves it on.
        using var play = BackupOverflowAnswer.Begin(BackupOverflowOperation.Launch);
        using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
        await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(TreeWhereAdoptingLosesFiles(), GameInstallation, preset: null, CancellationToken.None);

        prompt.Asked.Should().ContainSingle().Which.Operation.Should().Be(BackupOverflowOperation.AfterPlay);
    }

    [Fact]
    public async Task AfterPlayIngest_HonoursABackUpAnswerGivenBeforeTheLaunch()
    {
        var prompt = new RecordingPrompt(BackupOverflowChoice.Refuse);
        _prompt.Inner = prompt;
        var tree = new Dictionary<GamePath, SyncNode>
        {
            [new GamePath(LocationId.Game, "save/a.bin")] = MeasuredNode(0xB1, Actions.BackupFile | Actions.IngestFromDisk, kb: 6),
            [new GamePath(LocationId.Game, "save/b.bin")] = MeasuredNode(0xB2, Actions.BackupFile | Actions.IngestFromDisk, kb: 6),
        };

        using var play = BackupOverflowAnswer.Begin(BackupOverflowOperation.Launch);
        BackupOverflowAnswer.Active!.Remember(BackupOverflowChoice.BackUpAnyway);
        using var leg = BackupOverflowAnswer.BeginLeg(BackupOverflowOperation.AfterPlay, BackupOverflowChoice.AdoptInPlace);
        var (choice, asked) = await ((ALoadoutSynchronizer)Synchronizer).ResolveBackupOverflow(tree, GameInstallation, preset: null, CancellationToken.None);

        choice.Should().Be(BackupOverflowChoice.BackUpAnyway);
        asked.Should().BeFalse();
        prompt.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task DeclinedLaunchQuestion_ChangesNothingAndNamesTheOutcome()
    {
        WriteExternalFiles(4, 4096);
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, largeFiles: BackupOverflowChoice.AdoptInPlace);
        for (var i = 0; i < 3; i++) WriteExternalFile($"late{i}.bin", 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.Refuse);
        var filesBefore = Directory.EnumerateFiles(GameDir.ToString(), "*", SearchOption.AllDirectories).Count();

        using var play = BackupOverflowAnswer.Begin(BackupOverflowOperation.Launch);
        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        var declined = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        declined.Declined.Should().BeTrue();
        declined.Title.Should().Be("The game was not started");
        Directory.EnumerateFiles(GameDir.ToString(), "*", SearchOption.AllDirectories).Count().Should().Be(filesBefore);
    }

    [Fact]
    public async Task RefusedLoadoutSwitch_LeavesTheFolderAndTheLastSyncedLoadoutUntouched()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        var first = await CreateLoadout();
        var second = await LoadoutManager.CopyLoadout(first.LoadoutId);
        var filesBefore = Directory.EnumerateFiles(GameDir.ToString(), "*", SearchOption.AllDirectories).Count();

        var act = async () => await SynchronizerService.Synchronize(second.LoadoutId, largeFiles: BackupOverflowChoice.Refuse);

        await act.Should().ThrowAsync<BackupOverflowException>();
        Directory.EnumerateFiles(GameDir.ToString(), "*", SearchOption.AllDirectories).Count().Should().Be(filesBefore);
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).Value.Should().Be(first.LoadoutId);
    }
}

/// <summary>
/// The prompt the test host resolves; tests swap what answers after the host is built.
/// </summary>
public sealed class SwitchableBackupOverflowPrompt : IBackupOverflowPrompt
{
    public IBackupOverflowPrompt Inner { get; set; } = new RefusingBackupOverflowPrompt();

    public ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
        => Inner.Ask(installation, overflow, cancellationToken);
}

public sealed class RecordingPrompt(BackupOverflowChoice answer) : IBackupOverflowPrompt
{
    public List<BackupOverflow> Asked { get; } = [];

    public ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
    {
        Asked.Add(overflow);
        return ValueTask.FromResult(answer);
    }
}

public sealed class DelegatePrompt(Func<GameInstallation, BackupOverflow, CancellationToken, ValueTask<BackupOverflowChoice>> ask) : IBackupOverflowPrompt
{
    public ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken)
        => ask(installation, overflow, cancellationToken);
}
