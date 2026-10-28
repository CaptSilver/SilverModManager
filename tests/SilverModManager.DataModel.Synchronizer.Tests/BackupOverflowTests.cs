using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
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

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton(new BackupLimit(Size.KB * 8))
            .AddSingleton<IBackupOverflowPrompt>(_prompt);
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
        refusal.Message.Should().Contain("--large-files adopt").And.Contain("--large-files backup").And.Contain("16 KB");
        prompt.Asked.Should().BeEmpty();
        VisibleLoadouts.Should().Be(0);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
    }

    [Fact]
    public async Task PromptAnsweringRefuse_IsTheSameRefusal()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.Refuse);

        var act = async () => await CreateLoadout();

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.Declined.Should().BeTrue();
        refusal.Message.Should().Contain("--large-files adopt").And.Contain("16 KB");
        VisibleLoadouts.Should().Be(0);
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeFalse();
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
            // Only the first question starts the concurrent sync; a second one (from an unguarded sync that
            // re-indexed the same over-cap files) must show up in the count instead of starting another.
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
    public async Task UnmanageAfterAdopt_AsksAgainWithoutTheAdoptChoiceAndBacksUpBeforeDeleting()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();
        var prompt = new RecordingPrompt(BackupOverflowChoice.BackUpAnyway);
        _prompt.Inner = prompt;

        // No garbage collection: the copies are made before the delete, and unmanaging then lets go of them.
        await SynchronizerService.UnManage(GameInstallation, runGc: false, cleanGameFolder: true);

        prompt.Asked.Should().ContainSingle().Which.CanAdopt.Should().BeFalse();
        (await FileStore.HaveFile(HashOf("ext0.bin"))).Should().BeTrue();
        OnDisk("ext0.bin").Should().BeFalse();
    }

    [Fact]
    public async Task AdoptPresetWhereFilesWouldBeDeleted_IsRefusedReadably()
    {
        WriteExternalFiles(4, 4096);
        _prompt.Inner = new RecordingPrompt(BackupOverflowChoice.AdoptInPlace);
        await CreateLoadout();

        var act = async () => await SynchronizerService.UnManage(GameInstallation, cleanGameFolder: true, largeFiles: BackupOverflowChoice.AdoptInPlace);

        var refusal = (await act.Should().ThrowAsync<BackupOverflowException>()).Which;
        refusal.Message.Should().Contain("deleted").And.Contain("--large-files backup");
        for (var i = 0; i < 4; i++) OnDisk($"ext{i}.bin").Should().BeTrue();
        LoadoutManager.GetCurrentlyActiveLoadout(GameInstallation).HasValue.Should().BeTrue("the install stays managed");
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
