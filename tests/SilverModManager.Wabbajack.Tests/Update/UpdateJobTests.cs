using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Update;

/// <summary>The install job moving an installed list to another version of it, in place.</summary>
public class UpdateJobTests(ITestOutputHelper helper) : InstallTestBase<UpdateJobTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private const string MidUpdateMessage = "Finish the update with Resume, or undo it, before applying";

    private static (string Folder, string File, string Text) F(string folder, string file, string text) => (folder, file, text);

    /// <summary>Five files, so a folder holding them can be recognised again under another name.</summary>
    private static (string Folder, string File, string Text)[] FilesOf(string folder, string seed = "")
        => Enumerable.Range(0, 5).Select(i => F(folder, $"f{i}.txt", $"{folder}{seed} file {i}")).ToArray();

    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList(string modlistTxt, params IEnumerable<(string Folder, string File, string Text)>[] files)
    {
        var list = new ListBuilder();
        foreach (var (folder, file, text) in files.SelectMany(f => f)) list.Directives.Add(list.Inline($"mods/{folder}/{file}", text));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", modlistTxt));
        return await AddModlist(list.ToList("Test list"), list.Blobs);
    }

    private static InstallOptions UpdateOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => new(false, new HashSet<RelativePath>(), group.WabbajackModlistLoadoutGroupId);

    private WabbajackModlistLoadoutGroup.ReadOnly Fresh(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id);

    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> Update(Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly file, WabbajackModlistLoadoutGroup.ReadOnly group)
        => (await Install(loadout, file, UpdateOf(group))).Group;

    private IEnumerable<LoadoutItemGroup.ReadOnly> ModGroups(WabbajackModlistLoadoutGroup.ReadOnly group)
        => LoadoutItem.FindByParent(Connection.Db, group.Id).OfTypeLoadoutItemGroup();

    private LoadoutItemGroup.ReadOnly? ModGroup(WabbajackModlistLoadoutGroup.ReadOnly group, string name)
        => ModGroups(group).Where(g => g.AsLoadoutItem().Name == name).Select(g => (LoadoutItemGroup.ReadOnly?)g).FirstOrDefault();

    private async Task Disable(EntityId item)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(item, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private Dictionary<string, (EntityId Row, Hash Hash)> RowsOf(LoadoutItemGroup.ReadOnly mod)
        => LoadoutItem.FindByParent(Connection.Db, mod.Id).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .ToDictionary(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).Path.ToString(), f => (f.Id, f.Hash));

    [Fact]
    public async Task AnUpdateWritesARestorePointBeforeTheSwitchAndRecordsItOnTheGroup()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);

        var updated = await Update(loadout, two, group);

        updated.ModlistId.Value.Should().Be(two.Id);
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(updated, out var previous).Should().BeTrue();
        previous.Should().Be(one.Id);
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(updated, out var restorePoint).Should().BeTrue();
        var before = Connection.AsOf(TxId.From(restorePoint.Value));
        WabbajackModlistLoadoutGroup.Load(before, group.Id).ModlistId.Value.Should().Be(one.Id, "the restore point is the state before the switch");
        WabbajackModlistLoadoutGroup.Load(before, group.Id).InstalledAt.HasValue.Should().BeTrue();
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .Should().ContainSingle(row => row.Revision.Id.Value == restorePoint.Value && row.Reason.StartsWith("Wabbajack update of"));
        updated.InstalledAt.HasValue.Should().BeTrue("the update ran to its end");
    }

    [Fact]
    public async Task UnchangedFilesKeepTheirRowIdsAndStoreHashesAcrossAnUpdate()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A").Where(f => f.File != "f4.txt").Append(F("A", "f4.txt", "A changed")));
        var (_, group) = await Install(loadout, one, Options);
        var before = RowsOf(ModGroup(group, "A")!.Value);

        var updated = await Update(loadout, two, group);

        var after = RowsOf(ModGroup(updated, "A")!.Value);
        foreach (var path in before.Keys.Where(p => !p.EndsWith("f4.txt")))
            after[path].Should().Be(before[path], "a file the new list leaves alone is not rewritten");
        after.Single(p => p.Key.EndsWith("f4.txt")).Value.Hash.Should().NotBe(before.Single(p => p.Key.EndsWith("f4.txt")).Value.Hash);
        after.Should().HaveCount(before.Count, "the old row of the changed file is gone");
    }

    [Fact]
    public async Task ADroppedFolderAndItsGroupAreGoneAfterAnUpdateIncludingItsPriorityEntry()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n+C\n", FilesOf("A"), FilesOf("C"));
        var two = await AddList("+A\n", FilesOf("A"));
        var (_, group) = await Install(loadout, one, Options);
        var dropped = ModGroup(group, "C")!.Value.LoadoutItemGroupId;
        LoadoutItemGroupPriority.FindByTarget(Connection.Db, dropped).Should().ContainSingle();

        var updated = await Update(loadout, two, group);

        ModGroup(updated, "C").Should().BeNull();
        LoadoutItemGroup.Load(Connection.Db, dropped).IsValid().Should().BeFalse();
        LoadoutItemGroupPriority.FindByTarget(Connection.Db, dropped).Should().BeEmpty();
        ModGroup(updated, "A").Should().NotBeNull();
    }

    [Fact]
    public async Task ARenamedFolderKeepsItsGroupIdToggleAndRulesUnderTheNewName()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var renamed = FilesOf("A").Select(f => F("A SE", f.File, f.Text));
        var two = await AddList("-A SE\n", renamed);
        var (_, group) = await Install(loadout, one, Options);
        var mod = ModGroup(group, "A")!.Value;
        await Disable(mod.Id);
        var rows = RowsOf(mod);

        var updated = await Update(loadout, two, group);

        ModGroup(updated, "A").Should().BeNull();
        var now = ModGroup(updated, "A SE")!.Value;
        now.Id.Should().Be(mod.Id, "the group is the same one under a new name");
        WabbajackModGroup.Folder.TryGetValue(now, out var folder).Should().BeTrue();
        folder.Should().Be("A SE");
        now.AsLoadoutItem().IsDisabled.Should().BeTrue("the toggle belongs to the group");
        RowsOf(now).Should().BeEquivalentTo(rows, "no file was rewritten");
        LoadoutItemGroupPriority.FindByTarget(Connection.Db, now.LoadoutItemGroupId).Should().ContainSingle();
        ModGroups(updated).Should().HaveCount(1, "nothing was duplicated");
    }

    [Fact]
    public async Task AFolderBelowTheRenameThresholdIsDroppedAndAdded()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        // Two of the five files carry over: 40 percent, under the half the old folder must keep.
        var two = await AddList("+A SE\n", FilesOf("A").Take(2).Select(f => F("A SE", f.File, f.Text)), Enumerable.Range(0, 3).Select(i => F("A SE", $"new{i}.txt", $"new {i}")));
        var (_, group) = await Install(loadout, one, Options);
        var old = ModGroup(group, "A")!.Value.LoadoutItemGroupId;

        var updated = await Update(loadout, two, group);

        ModGroup(updated, "A").Should().BeNull();
        LoadoutItemGroup.Load(Connection.Db, old).IsValid().Should().BeFalse();
        ModGroup(updated, "A SE")!.Value.LoadoutItemGroupId.Should().NotBe(old);
    }

    [Fact]
    public async Task AListTheUserSwitchedOffStaysOffAfterAnUpdate()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);
        group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeFalse();
        await Disable(group.Id);

        var updated = await Update(loadout, two, group);

        updated.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue();
        updated.InstalledAt.HasValue.Should().BeTrue();
        WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(updated, out _).Should().BeFalse("the note is taken back once the update has ended");
    }

    [Fact]
    public async Task AListDisabledOnlyByMissingArchivesIsEnabledAfterAnUpdateThatSuppliesThem()
    {
        var loadout = await CreateLoadout();
        var absent = new ArchiveEntry(Xx64.From(77), 10, "absent.zip", "", new HttpState(new Uri("https://example.invalid/absent.zip"), []));
        var present = await AddZip("present.zip", new() { ["x.bin"] = [1, 2, 3, 4] });
        var listOne = new ListBuilder();
        listOne.Archives.Add(absent);
        listOne.Directives.Add(listOne.Member("mods/A/x.bin", absent, "x.bin", [1, 2, 3, 4]));
        listOne.Directives.Add(listOne.Inline("profiles/Default/modlist.txt", "+A\n"));
        var listTwo = new ListBuilder();
        listTwo.Archives.Add(present);
        listTwo.Directives.Add(listTwo.Member("mods/A/x.bin", present, "x.bin", [1, 2, 3, 4]));
        listTwo.Directives.Add(listTwo.Inline("profiles/Default/modlist.txt", "+A\n"));
        var one = await AddModlist(listOne.ToList("Test list"), listOne.Blobs);
        var two = await AddModlist(listTwo.ToList("Test list"), listTwo.Blobs);
        var (_, group) = await Install(loadout, one, new InstallOptions(true, new HashSet<RelativePath>()));
        group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue();
        SkipsOf(group).Should().ContainSingle().Which.Kind.IsByDesign().Should().BeFalse();

        var updated = await Update(loadout, two, group);

        SkipsOf(updated).Should().BeEmpty();
        updated.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeFalse("nothing the user chose keeps it off");
    }

    [Fact]
    public async Task AUsersOwnModOutsideTheGroupIsUntouchedByAnUpdate()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A").Take(2), FilesOf("B"));
        var archive = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("Data/outside.txt")] = "outside"u8.ToArray() });
        var library = await RegisterLocalArchive(archive.Path);
        var installed = await LoadoutManager.InstallItem(library.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId);
        var outside = installed.LoadoutItemGroup!.Value;
        var (_, group) = await Install(loadout, one, Options);
        var before = RowsOf(LoadoutItemGroup.Load(Connection.Db, outside.LoadoutItemGroupId));

        await Update(loadout, two, group);

        var after = LoadoutItemGroup.Load(Connection.Db, outside.LoadoutItemGroupId);
        after.IsValid().Should().BeTrue();
        after.AsLoadoutItem().ParentId.Should().Be(outside.AsLoadoutItem().ParentId);
        RowsOf(after).Should().BeEquivalentTo(before);
    }

    /// <summary>Forwards to a store, but every call to back files up after the first <c>allowed</c> ones fails.</summary>
    private sealed class FailingAfter(IFileStore inner, int allowed) : ForwardingFileStore(inner)
    {
        private int _calls;

        public int Calls => _calls;

        public override Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default, IProgress<double>? progress = null)
            => Interlocked.Increment(ref _calls) > allowed ? throw new IOException("The disk is full") : base.BackupFiles(backups, deduplicate, token, progress);
    }

    [Fact]
    public async Task AnUpdateInterruptedByAnIOExceptionResumesAsAnUpdateFromTheGroupAlone()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n+B\n", FilesOf("A"), Enumerable.Range(0, 8).Select(i => F("B", $"b{i}.txt", $"B Resume file {i}")));
        var (_, group) = await Install(loadout, one, Options);
        await Disable(group.Id);

        var failing = new FailingAfter(FileStore, 1);
        var job = NewJob(loadout, two, UpdateOf(group), writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        var failure = await Record.ExceptionAsync(() => Run(job));

        failure.Should().BeOfType<IOException>($"the store was asked to back up files {failing.Calls} times");
        var stopped = Fresh(group);
        stopped.InstalledAt.HasValue.Should().BeFalse();
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(stopped, out var previous).Should().BeTrue();
        previous.Should().Be(one.Id);
        stopped.ModlistId.Value.Should().Be(two.Id);

        // The caller no longer says it is an update: the group does.
        var (_, resumed) = await Install(loadout, two, Options);

        resumed.Id.Should().Be(group.Id);
        resumed.InstalledAt.HasValue.Should().BeTrue();
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(resumed, out _).Should().BeTrue("the update stays undoable");
        resumed.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue("the user had switched it off");
        TreeOf(resumed)["B"].Should().HaveCount(8);
    }

    private async Task LeaveMidUpdate(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly previous)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.PreviousModlist, previous.Id);
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.WasDisabledByUser, false);
        tx.Retract(group.Id, WabbajackModlistLoadoutGroup.InstalledAt, group.InstalledAt.Value);
        await tx.Commit();
    }

    [Fact]
    public async Task ApplyIsRefusedWhileAnUpdateIsUnfinished()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var (_, group) = await Install(loadout, one, Options);
        var runs = ServiceProvider.GetRequiredService<ModlistInstallRuns>();
        runs.IsInstallingInto(loadout.LoadoutId).Should().BeFalse();

        await LeaveMidUpdate(group, one);

        runs.FindInto(loadout.LoadoutId).Should().BeNull("no job is running");
        runs.IsInstallingInto(loadout.LoadoutId).Should().BeTrue();
        runs.InstallBlockReason(loadout.LoadoutId).Should().Be(MidUpdateMessage);
        var refused = await Record.ExceptionAsync(async () => await Synchronizer.Synchronize(Loadout.Load(Connection.Db, loadout.LoadoutId)));
        refused.Should().BeOfType<LoadoutInstallRunningException>().Which.Message.Should().Be(MidUpdateMessage);
    }

    private (IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> Task, TaskCompletionSource Gate) BeginHeld(InstallModlistJob job)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job, async _ =>
        {
            await gate.Task;
            return default;
        });
        return (task, gate);
    }

    [Fact]
    public async Task ASecondUpdateOrAReinstallIsRefusedWhileOneRuns()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);
        var (running, gate) = BeginHeld(NewJob(loadout, two, UpdateOf(group)));

        var secondUpdate = await Record.ExceptionAsync(async () => await InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, two, UpdateOf(group)));
        var reinstall = await Record.ExceptionAsync(async () => await InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, one, Options));

        secondUpdate.Should().BeOfType<InstallAlreadyRunningException>();
        reinstall.Should().BeOfType<InstallAlreadyRunningException>();
        gate.SetResult();
        await running;
    }

    [Fact]
    public async Task AFinishedUpdateWhoseInstalledAtIsRetractedAgainIsNotMidUpdate()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);
        var updated = await Update(loadout, two, group);
        var runs = ServiceProvider.GetRequiredService<ModlistInstallRuns>();
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(updated, out _).Should().BeTrue();

        // What a reinstall does first: take the group's install mark back.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(updated.Id, WabbajackModlistLoadoutGroup.InstalledAt, updated.InstalledAt.Value);
            await tx.Commit();
        }

        runs.IsInstallingInto(loadout.LoadoutId).Should().BeFalse("a plain reinstall is not an update");
        runs.InstallBlockReason(loadout.LoadoutId).Should().BeNull();
        var (_, reinstalled) = await Install(loadout, two, Options);
        reinstalled.InstalledAt.HasValue.Should().BeTrue();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(reinstalled, out var restorePoint).Should().BeTrue();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(updated, out var before).Should().BeTrue();
        restorePoint.Should().Be(before, "no second restore point was written");
    }

    [Fact]
    public async Task ASecondUpdateKeepsTheProtectionOfTheFirstUntilTheUpdateIsFinishedByTheUser()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var three = await AddList("+A\n", FilesOf("A"), FilesOf("B"), FilesOf("C"));
        var (_, group) = await Install(loadout, one, Options);
        var second = await Update(loadout, two, group);
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(second, out var first).Should().BeTrue();

        var third = await Update(loadout, three, second);

        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(third, out var latest).Should().BeTrue();
        latest.Should().NotBe(first);
        var protectedRevisions = ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(r => r.Revision.Id.Value).ToArray();
        protectedRevisions.Should().Contain([first.Value, latest.Value], "an undo of the later update lands on the group that still needs the first one's files");
    }

    [Fact]
    public async Task ARestorePointFromAnUpdateThatStoppedPartWayAppliesAgainOnceTheUserUndoes()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n+B\n", FilesOf("A"), Enumerable.Range(0, 8).Select(i => F("B", $"b{i}.txt", $"B Undo file {i}")));
        var (_, group) = await Install(loadout, one, Options);
        var failing = new FailingAfter(FileStore, 0);
        var job = NewJob(loadout, two, UpdateOf(group), writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        (await Record.ExceptionAsync(() => Run(job))).Should().BeOfType<IOException>($"the store was asked to back up files {failing.Calls} times");
        var runs = ServiceProvider.GetRequiredService<ModlistInstallRuns>();
        runs.IsInstallingInto(loadout.LoadoutId).Should().BeTrue("the update is unfinished");
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(Fresh(group), out var restorePoint).Should().BeTrue();

        await ServiceProvider.GetRequiredService<UndoService>().RevertTo(new LoadoutRevision(loadout.Id, restorePoint, DateTimeOffset.UtcNow));

        var reverted = Fresh(group);
        reverted.ModlistId.Value.Should().Be(one.Id);
        reverted.InstalledAt.HasValue.Should().BeTrue();
        runs.IsInstallingInto(loadout.LoadoutId).Should().BeFalse();
        var applied = await Record.ExceptionAsync(async () => await Synchronizer.Synchronize(Loadout.Load(Connection.Db, loadout.LoadoutId)));
        (applied is LoadoutInstallRunningException).Should().BeFalse("Apply is allowed again");
    }

    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddListWithRootDlls(string[] dlls, params IEnumerable<(string Folder, string File, string Text)>[] files)
    {
        var list = new ListBuilder();
        foreach (var dll in dlls)
        {
            var archive = await AddZip($"{dll}.zip", new() { [dll] = System.Text.Encoding.UTF8.GetBytes($"stock {dll}") });
            list.Archives.Add(archive);
            list.Directives.Add(list.Member($"Stock Game/{dll}", archive, dll, System.Text.Encoding.UTF8.GetBytes($"stock {dll}")));
        }
        // The game's own executable in the folder is what makes "Stock Game" a copy of the game.
        var exe = GameExeArchive();
        list.Archives.Add(exe);
        list.Directives.Add(new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(exe.XxHash64, [])));
        list.Directives.Add(list.Remapped("ModOrganizer.ini", "[General]\nselected_profile=@ByteArray(Default)\n"));
        list.Directives.Add(list.Inline("ModOrganizer.exe", "exe"));
        var all = files.SelectMany(f => f).ToArray();
        foreach (var (folder, file, text) in all) list.Directives.Add(list.Inline($"mods/{folder}/{file}", text));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", string.Concat(all.Select(f => f.Folder).Distinct().Select(f => $"+{f}\n"))));
        return await AddModlist(list.ToList("Test list"), list.Blobs);
    }

    private static InstallOptions Keeping(string file) => new(false, new HashSet<RelativePath> { P(file) });

    [Fact]
    public async Task AnUpdateWritesAGameRootFileTheNewVersionAddsAndStillKeepsOneTheUserKeptFromTheOldOne()
    {
        var loadout = await CreateLoadout();
        var one = await AddListWithRootDlls(["d3d11.dll"], FilesOf("A"));
        var two = await AddListWithRootDlls(["d3d11.dll", "d3d12.dll"], FilesOf("A"));
        var (_, group) = await Install(loadout, one, Keeping("d3d11.dll"));
        TreeOf(group).Should().NotContainKey("Stock Game");

        var updated = await Update(loadout, two, group);

        TreeOf(updated)["Stock Game"].Keys.Should().BeEquivalentTo(["Game/d3d12.dll"], "the new file is written and the one the user kept is not");
    }

    [Fact]
    public async Task AnUpdateResumedWithoutOptionsStillKeepsTheGameFilesTheUserKept()
    {
        var loadout = await CreateLoadout();
        var one = await AddListWithRootDlls(["d3d11.dll"], FilesOf("A"));
        var two = await AddListWithRootDlls(["d3d11.dll"], FilesOf("A"), Enumerable.Range(0, 8).Select(i => F("B", $"b{i}.txt", $"B Kept file {i}")));
        var (_, group) = await Install(loadout, one, Keeping("d3d11.dll"));
        var failing = new FailingAfter(FileStore, 0);
        var job = NewJob(loadout, two, UpdateOf(group), writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        (await Record.ExceptionAsync(() => Run(job))).Should().BeOfType<IOException>($"the store was asked to back up files {failing.Calls} times");

        var (_, resumed) = await Install(loadout, two, Options);

        resumed.InstalledAt.HasValue.Should().BeTrue();
        TreeOf(resumed).Should().NotContainKey("Stock Game", "the stock file the user kept is not replaced by the resume");
        TreeOf(resumed)["B"].Should().HaveCount(8);
    }

    [Fact]
    public async Task UpdatingToTheFileAlreadyInstalledIsRefusedBeforeAnyWrite()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var (_, group) = await Install(loadout, one, Options);
        var basis = Connection.Db.BasisTxId;
        var tree = TreeOf(group);

        var refused = await Record.ExceptionAsync(async () => await Update(loadout, one, group));

        refused.Should().BeOfType<ModlistUpdateRefusedException>();
        Connection.Db.BasisTxId.Should().Be(basis, "no restore point and no switch were written");
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(Fresh(group), out _).Should().BeFalse();
        TreeOf(Fresh(group)).Should().BeEquivalentTo(tree);
    }

    [Fact]
    public async Task UpdatingAGroupOfAnotherLoadoutIsRefused()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);

        var refused = await Record.ExceptionAsync(async () => await Update(other, two, group));

        refused.Should().BeOfType<ModlistUpdateRefusedException>();
        Fresh(group).ModlistId.Value.Should().Be(one.Id);
    }

    [Fact]
    public async Task UpdatingAGroupThatIsMidUpdateToAnotherFileIsRefusedUntilItIsFinished()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n", FilesOf("A"), FilesOf("B"));
        var three = await AddList("+A\n", FilesOf("A"), FilesOf("C"));
        var (_, group) = await Install(loadout, one, Options);
        await LeaveMidUpdate(group, one);

        var refused = await Record.ExceptionAsync(async () => await Update(loadout, two, Fresh(group)));
        var elsewhere = await Record.ExceptionAsync(async () => await Update(loadout, three, Fresh(group)));

        refused.Should().BeOfType<ModlistUpdateRefusedException>().Which.Message.Should().Contain("Finish the update with Resume, or undo it");
        elsewhere.Should().BeOfType<ModlistUpdateRefusedException>();
    }
}
