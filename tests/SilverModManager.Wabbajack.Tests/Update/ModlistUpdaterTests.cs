using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Gallery;
using SilverModManager.Wabbajack.Tests.Install;
using SilverModManager.Wabbajack.Update;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Update;

/// <summary>Moving an installed list to a newer version of it: review, download, apply under a restore point, undo and finish.</summary>
public class ModlistUpdaterTests(ITestOutputHelper helper) : UpdateCheckTestBase<ModlistUpdaterTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private static readonly IReadOnlySet<RelativePath> NoKeptGameFiles = new HashSet<RelativePath>();

    private ModlistUpdater Updater => new(ServiceProvider);

    private ModlistUpdateFinish Finisher => new(ServiceProvider);

    private static (string Folder, string File, string Text) F(string folder, string file, string text) => (folder, file, text);

    private static (string Folder, string File, string Text)[] FilesOf(string folder, string seed = "")
        => Enumerable.Range(0, 5).Select(i => F(folder, $"f{i}.txt", $"{folder}{seed} file {i}")).ToArray();

    private static byte[] Bytes(int seed, int length = 40) => Enumerable.Range(0, length).Select(i => (byte)(seed * 7 + i)).ToArray();

    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList(string modlistTxt, params IEnumerable<(string Folder, string File, string Text)>[] files)
    {
        var list = new ListBuilder();
        foreach (var (folder, file, text) in files.SelectMany(f => f)) list.Directives.Add(list.Inline($"mods/{folder}/{file}", text));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", modlistTxt));
        return await AddModlist(list.ToList("Test list"), list.Blobs);
    }

    private WabbajackModlistLoadoutGroup.ReadOnly Fresh(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id);

    private Task<WabbajackModlistLoadoutGroup.ReadOnly> ApplyUpdate(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile)
        => Updater.Apply(group, newFile, false, NoKeptGameFiles, CancellationToken.None);

    private Func<TxId, Task> RevertFor(Loadout.ReadOnly loadout)
    {
        var undo = ServiceProvider.GetRequiredService<UndoService>();
        return tx => undo.RevertTo(new LoadoutRevision(loadout.LoadoutId.Value, EntityId.From(tx.Value), DateTimeOffset.UtcNow));
    }

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly One, WabbajackModlistLibraryFile.ReadOnly Two, WabbajackModlistLoadoutGroup.ReadOnly Group)> InstalledVersionOne()
    {
        var loadout = await CreateLoadout();
        var one = await AddList("+A\n", FilesOf("A"));
        var two = await AddList("+A\n+B\n", FilesOf("A").Where(f => f.File != "f4.txt").Append(F("A", "f4.txt", "A changed in two")), FilesOf("B"));
        var (_, group) = await Install(loadout, one, Options);
        return (loadout, one, two, group);
    }

    /// <summary>
    /// Version one writes a patched file that exists in no library file (the patch result is made by the install), and
    /// version two patches it into something else, so the bytes of version one's result are held only by the restore point.
    /// </summary>
    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly Two, WabbajackModlistLoadoutGroup.ReadOnly Group)> InstalledPatchedVersionOne()
    {
        var basis = Bytes(60, 200);
        var archive = await AddZip("base.zip", new Dictionary<string, byte[]> { ["base.bin"] = basis });
        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(one.Patched("mods/A/p.bin", archive, "base.bin", basis, [.. basis, .. Bytes(61, 30)]));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.Add(archive);
        two.Directives.Add(two.Patched("mods/A/p.bin", archive, "base.bin", basis, [.. basis, .. Bytes(62, 30)]));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, installed, Options);
        return (loadout, newFile, group);
    }

    private async Task LeaveMidUpdate(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly previous)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.PreviousModlist, previous.Id);
        tx.Add(group.Id, WabbajackModlistLoadoutGroup.WasDisabledByUser, false);
        tx.Retract(group.Id, WabbajackModlistLoadoutGroup.InstalledAt, group.InstalledAt.Value);
        await tx.Commit();
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

    private static InstallOptions UpdateOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => new(false, new HashSet<RelativePath>(), group.WabbajackModlistLoadoutGroupId);

    /// <summary>Matching the list's game files may record digests the first time; only the call under test has to leave everything alone.</summary>
    private async Task WarmInventory(Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly file)
    {
        file.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        var list = await ModlistArchive.ReadModlist(ServiceProvider.GetRequiredService<IFileStore>(), archive, CancellationToken.None);
        var inventory = ServiceProvider.GetRequiredService<ArchiveInventory>();
        await inventory.Build(list, Connection.Db, inventory.GameFilesFor(loadout));
    }

    private async Task<bool> HaveInStore(Hash hash) => await FileStore.HaveFile(FileIdentity.XxHash3(hash));

    private IEnumerable<Hash> HashesOf(WabbajackModlistLoadoutGroup.ReadOnly group) => TreeOf(group).Values.SelectMany(files => files.Values).Distinct();

    [Fact]
    public async Task ApplyWithAMissingArchiveAndNoSkipIsRefusedWithoutARestorePointOrAnyDatomChange()
    {
        var files = new Dictionary<string, byte[]> { ["a0.bin"] = Bytes(1) };
        var archive = await AddZip("a.zip", files);
        var absent = new ArchiveEntry(Xx64.From(99), 1234, "new.zip", "", new HttpState(new Uri("https://example.invalid/new.zip"), []));
        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(one.Member("mods/A/a0.bin", archive, "a0.bin", files["a0.bin"]));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.AddRange([archive, absent]);
        two.Directives.Add(two.Member("mods/A/a0.bin", archive, "a0.bin", files["a0.bin"]));
        two.Directives.Add(two.Member("mods/N/n0.bin", absent, "n0.bin", Bytes(50)));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n+N\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, installed, Options);
        await WarmInventory(loadout, newFile);
        var basis = Connection.Db.BasisTxId;

        var refused = await Record.ExceptionAsync(() => Updater.Apply(group, newFile, false, NoKeptGameFiles, CancellationToken.None));

        refused.Should().BeOfType<ModlistUpdateRefusedException>().Which.Message.Should()
            .Contain("1 archives are still missing").And.Contain("new.zip").And.EndWith("nothing was changed");
        Connection.Db.BasisTxId.Should().Be(basis, "no restore point and no datom was written");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        Fresh(group).ModlistId.Value.Should().Be(installed.Id);
    }

    [Fact]
    public async Task ApplyToTheInstalledFileIsRefused()
    {
        var (loadout, one, _, group) = await InstalledVersionOne();
        var basis = Connection.Db.BasisTxId;

        var refused = await Record.ExceptionAsync(() => ApplyUpdate(group, one));

        refused.Should().BeOfType<ModlistUpdateRefusedException>();
        Connection.Db.BasisTxId.Should().Be(basis);
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyOfAnEntryTheGalleryHasSwitchedOffIsRefusedBeforeAnyWrite()
    {
        var (_, one, two, group) = await InstalledVersionOne();
        await WithIdentity(Connection, one, "a-list", "wabbajack-tools/mod-lists");
        two = await WithIdentity(Connection, two, "a-list", "wabbajack-tools/mod-lists");
        Gallery.Cached = Listing(GalleryLibraryFiles.Entry("Test list") with { IsForcedDown = true });
        var basis = Connection.Db.BasisTxId;

        var refused = await Record.ExceptionAsync(() => ApplyUpdate(Fresh(group), two));

        refused.Should().BeOfType<ModlistUpdateRefusedException>();
        Connection.Db.BasisTxId.Should().Be(basis);
    }

    [Fact]
    public async Task ApplyWritesTheRestorePointAndSwitchesThenInstallsTheNewVersion()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();

        var updated = await ApplyUpdate(group, two);

        updated.Id.Should().Be(group.Id);
        updated.ModlistId.Value.Should().Be(two.Id);
        updated.InstalledAt.HasValue.Should().BeTrue();
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(updated, out var previous).Should().BeTrue();
        previous.Should().Be(one.Id);
        var restorePoint = Updater.RestorePointOf(updated);
        restorePoint.Should().NotBeNull();
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .Should().ContainSingle(row => row.Revision.Id.Value == restorePoint!.Value.Value);
        TreeOf(updated).Should().ContainKey("B");
        WabbajackModlistLoadoutGroup.Load(Connection.AsOf(restorePoint!.Value), group.Id).ModlistId.Value.Should().Be(one.Id);
    }

    [Fact]
    public async Task ReviewDescribesTheUpdateAndWritesNothing()
    {
        var (loadout, _, two, group) = await InstalledVersionOne();
        await WarmInventory(loadout, two);
        var basis = Connection.Db.BasisTxId;

        var review = await Updater.Review(group, two, CancellationToken.None);

        review.Added.Should().Equal("B");
        review.Changed.Should().Equal("A");
        Connection.Db.BasisTxId.Should().Be(basis);
        Updater.RestorePointOf(Fresh(group)).Should().BeNull("a review is not an update");
    }

    [Fact]
    public async Task ARestorePointIsOnlyRecordedOnceTheGroupIsSwitched()
    {
        var (_, _, two, group) = await InstalledVersionOne();
        Updater.RestorePointOf(group).Should().BeNull();

        var updated = await ApplyUpdate(group, two);

        Updater.RestorePointOf(updated).Should().NotBeNull();
    }

    [Fact]
    public async Task RevertingToTheRestorePointBringsBackTheOldRowsAndModlist()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        var before = TreeOf(group);
        var updated = await ApplyUpdate(group, two);
        TreeOf(updated).Should().NotBeEquivalentTo(before);
        var restorePoint = Updater.RestorePointOf(updated)!.Value;

        await Updater.Undo(updated, RevertFor(loadout), CancellationToken.None);

        var reverted = Fresh(group);
        reverted.ModlistId.Value.Should().Be(one.Id);
        TreeOf(reverted).Should().BeEquivalentTo(before);
        reverted.InstalledAt.HasValue.Should().BeTrue();
        restorePoint.Value.Should().NotBe(0UL);
    }

    [Fact]
    public async Task RevertingAlsoRemovesTheProtectionRowThePreviousModlistReferenceAndTheNewVersionsSkipRows()
    {
        var loadout = await CreateLoadout();
        var one = new ListBuilder();
        one.Directives.Add(one.Inline("mods/A/a.txt", "A"));
        one.Directives.Add(UnbuildableArchive("mods/A/old.bsa"));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Directives.Add(two.Inline("mods/A/a.txt", "A"));
        two.Directives.Add(UnbuildableArchive("mods/A/built.bsa"));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(loadout, installed, Options);
        var skipsBefore = SkipsOf(group).Select(s => (s.To, s.Kind)).ToArray();
        skipsBefore.Should().ContainSingle(s => s.To == "mods/A/old.bsa");

        var updated = await Updater.Apply(group, newFile, true, NoKeptGameFiles, CancellationToken.None);
        SkipsOf(updated).Should().NotBeEmpty("the new version skips the archive it cannot build");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();

        await Updater.Undo(updated, RevertFor(loadout), CancellationToken.None);

        SkipsOf(group).Select(s => (s.To, s.Kind)).Should().BeEquivalentTo(skipsBefore);
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        var reverted = Fresh(group);
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(reverted, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(reverted, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(reverted, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UndoAndFinishAreRefusedWhileTheUpdateIsUnfinished()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        var updated = await ApplyUpdate(group, two);
        var (held, gate) = BeginHeld(NewJob(loadout, two, UpdateOf(updated)));
        try
        {
            var undo = await Record.ExceptionAsync(() => Updater.Undo(Fresh(group), RevertFor(loadout), CancellationToken.None));
            var finish = await Record.ExceptionAsync(() => Finisher.Finish(Fresh(group), true, CancellationToken.None));

            undo.Should().BeOfType<ModlistUpdateRefusedException>("a job is writing into the loadout");
            finish.Should().BeOfType<ModlistUpdateRefusedException>();
        }
        finally
        {
            gate.SetResult();
            await held;
        }

        await LeaveMidUpdate(Fresh(group), one);
        var midFinish = await Record.ExceptionAsync(() => Finisher.Finish(Fresh(group), true, CancellationToken.None));
        midFinish.Should().BeOfType<ModlistUpdateRefusedException>("releasing the restore point over a half-written version would lose the way back");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnUpdateLeftHalfWrittenCanStillBeUndone()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        var before = TreeOf(group);
        var failing = new UpdateJobTests.FailingAfter(FileStore, 1);
        var job = NewJob(loadout, two, UpdateOf(group), writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        (await Record.ExceptionAsync(() => Run(job))).Should().NotBeNull();
        ModlistInstallRuns.IsMidUpdate(Fresh(group)).Should().BeTrue();

        await Updater.Undo(Fresh(group), RevertFor(loadout), CancellationToken.None);

        var reverted = Fresh(group);
        ModlistInstallRuns.IsMidUpdate(reverted).Should().BeFalse();
        reverted.ModlistId.Value.Should().Be(one.Id);
        TreeOf(reverted).Should().BeEquivalentTo(before);
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task UndoAfterApplyAndGarbageCollectionStillHasTheOldVersionsBytes()
    {
        var (loadout, two, group) = await InstalledPatchedVersionOne();
        var oldHashes = HashesOf(group).ToArray();
        var updated = await ApplyUpdate(group, two);
        var gone = oldHashes.Except(HashesOf(updated)).ToArray();
        gone.Should().NotBeEmpty("the update replaced a file, so some of version one's bytes are no longer in the loadout");

        await ServiceProvider.GetRequiredService<IGarbageCollectorRunner>().RunAsync();
        await Updater.Undo(updated, RevertFor(loadout), CancellationToken.None);

        foreach (var hash in HashesOf(Fresh(group))) (await HaveInStore(hash)).Should().BeTrue("a restored row must still have its bytes");
        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnrelatedLibraryRemovalDoesNotCollectTheProtectedBytes()
    {
        var (_, two, group) = await InstalledPatchedVersionOne();
        var oldHashes = HashesOf(group).ToArray();
        var updated = await ApplyUpdate(group, two);
        var gone = oldHashes.Except(HashesOf(updated)).ToArray();
        gone.Should().NotBeEmpty();
        await using var unrelated = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("x.txt")] = "unrelated"u8.ToArray() });
        var library = await RegisterLocalArchive(unrelated.Path);

        await ServiceProvider.GetRequiredService<ILibraryService>().RemoveLibraryItems([library.AsLibraryFile().AsLibraryItem()], GarbageCollectorRunMode.RunSynchronously);

        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeTrue("the restore point protects them");
    }

    [Fact]
    public async Task AfterFinishUpdateGarbageCollectionTakesTheBytesOnlyTheOldVersionHeld()
    {
        var (_, two, group) = await InstalledPatchedVersionOne();
        var oldHashes = HashesOf(group).ToArray();
        var updated = await ApplyUpdate(group, two);
        var gone = oldHashes.Except(HashesOf(updated)).ToArray();
        gone.Should().NotBeEmpty();
        var collector = ServiceProvider.GetRequiredService<IGarbageCollectorRunner>();
        await collector.RunAsync();
        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeTrue("the restore point is still protected");

        await Finisher.Finish(updated, deleteOldArchives: false, CancellationToken.None);
        await collector.RunAsync();

        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeFalse("nothing refers to them once the protection is gone");
        foreach (var hash in HashesOf(Fresh(group))) (await HaveInStore(hash)).Should().BeTrue();
    }

    /// <summary>Version one names a shared archive and one only it uses, both fetched for it; version two keeps the shared one and adds another. Version one is installed.</summary>
    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLibraryFile.ReadOnly Installed, WabbajackModlistLibraryFile.ReadOnly NewFile, WabbajackModlistLoadoutGroup.ReadOnly Group)> InstalledWithAnOldOnlyArchive()
    {
        var loadout = await CreateLoadout();
        var shared = await AddZip("shared.zip", new Dictionary<string, byte[]> { ["s.bin"] = Bytes(1) });
        var oldOnly = await AddZip("old-only.zip", new Dictionary<string, byte[]> { ["o.bin"] = Bytes(2) });
        var newOnly = await AddZip("new-only.zip", new Dictionary<string, byte[]> { ["n.bin"] = Bytes(3) });
        var one = new ListBuilder();
        one.Archives.AddRange([shared, oldOnly]);
        one.Directives.Add(one.Member("mods/A/s.bin", shared, "s.bin", Bytes(1)));
        one.Directives.Add(one.Member("mods/A/o.bin", oldOnly, "o.bin", Bytes(2)));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.AddRange([shared, newOnly]);
        two.Directives.Add(two.Member("mods/A/s.bin", shared, "s.bin", Bytes(1)));
        two.Directives.Add(two.Member("mods/A/n.bin", newOnly, "n.bin", Bytes(3)));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        await MarkFetched(installed, shared, oldOnly);
        var (_, group) = await Install(loadout, installed, Options);
        return (loadout, installed, newFile, group);
    }

    [Fact]
    public async Task FinishUpdateDropsTheProtectionAndOffersOnlyOldVersionOnlyArchives()
    {
        var (loadout, installed, newFile, group) = await InstalledWithAnOldOnlyArchive();
        var updated = await ApplyUpdate(group, newFile);

        var plan = Finisher.Plan(updated);

        plan.HasUndo.Should().BeTrue();
        plan.OldVersion.Modlist.Value.Should().Be(installed.Id);
        plan.OldVersion.Groups.Should().BeEmpty();
        plan.OldVersion.OwnedArchives.Should().ContainSingle();
        plan.OldVersion.Kept.Should().ContainSingle(k => k.Name == "shared.zip" && k.Reason.StartsWith("used by"));

        await Finisher.Finish(updated, deleteOldArchives: true, CancellationToken.None);

        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        var finished = Fresh(group);
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(finished, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(finished, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(finished, out _).Should().BeFalse();
        finished.ModlistId.Value.Should().Be(newFile.Id, "the group stays on the new version");
        WabbajackModlistLibraryFile.Load(Connection.Db, installed.Id).IsValid().Should().BeFalse("the old list file went with the old archives");
        LibraryFile.Load(Connection.Db, oldOnlyLibraryId).IsValid().Should().BeFalse();
        LibraryFile.Load(Connection.Db, sharedLibraryId).IsValid().Should().BeTrue("the new version names it too");
        Updater.RestorePointOf(finished).Should().BeNull();
        Finisher.Plan(finished).HasUndo.Should().BeFalse();
    }

    private EntityId oldOnlyLibraryId;
    private EntityId sharedLibraryId;

    /// <summary>Records that the app fetched these archives for <paramref name="list"/>, as a download from the gallery would.</summary>
    private async Task MarkFetched(WabbajackModlistLibraryFile.ReadOnly list, ArchiveEntry shared, ArchiveEntry oldOnly)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var row in WabbajackModlistArchive.FindByModlist(Connection.Db, list.Id))
        {
            var file = ServiceProvider.GetRequiredService<ILibraryArchiveLocator>().FindTopLevelByXxHash64(Connection.Db, row.Identity);
            if (file is null) continue;
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file.Value.Id);
            if (row.Identity.Equals(shared.XxHash64)) sharedLibraryId = file.Value.Id;
            if (row.Identity.Equals(oldOnly.XxHash64)) oldOnlyLibraryId = file.Value.Id;
        }
        await tx.Commit();
    }

    [Fact]
    public async Task FinishUpdateWithoutDeletingKeepsTheOldFileAndItsArchives()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        var updated = await ApplyUpdate(group, two);

        await Finisher.Finish(updated, deleteOldArchives: false, CancellationToken.None);

        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        WabbajackModlistLibraryFile.Load(Connection.Db, one.Id).IsValid().Should().BeTrue();
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(Fresh(group), out _).Should().BeFalse();
    }

    [Fact]
    public async Task FinishUpdateIsTheOnlyPathThatRemovesTheOldArchives()
    {
        var (loadout, installed, newFile, group) = await InstalledWithAnOldOnlyArchive();

        var updated = await ApplyUpdate(group, newFile);
        await ServiceProvider.GetRequiredService<IGarbageCollectorRunner>().RunAsync();

        WabbajackModlistLibraryFile.Load(Connection.Db, installed.Id).IsValid().Should().BeTrue("applying the update leaves the old list file in the library");
        WabbajackModlistArchive.FindByModlist(Connection.Db, installed.Id).Should().NotBeEmpty();
        LibraryFile.Load(Connection.Db, oldOnlyLibraryId).IsValid().Should().BeTrue("neither the apply nor a collection takes the old version's archive");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle("only Finish update releases the root");
        updated.InstalledAt.HasValue.Should().BeTrue();

        await Finisher.Finish(updated, deleteOldArchives: true, CancellationToken.None);

        LibraryFile.Load(Connection.Db, oldOnlyLibraryId).IsValid().Should().BeFalse("finishing is what removes it");
    }

    [Fact]
    public async Task FinishingOneLoadoutsUpdateLeavesTheInstallOfAnotherLoadoutThatIsStillUpdatingFromTheOldFile()
    {
        var (_, installed, newFile, group) = await InstalledWithAnOldOnlyArchive();
        var other = await CreateLoadout();
        var (_, otherGroup) = await Install(other, installed, Options);
        var updated = await ApplyUpdate(group, newFile);
        var otherUpdated = await ApplyUpdate(otherGroup, newFile);

        var plan = Finisher.Plan(updated);
        await Finisher.Finish(updated, deleteOldArchives: true, CancellationToken.None);

        plan.OldVersion.UnfinishedUpdates.Should().ContainSingle();
        plan.OldVersion.OwnedArchives.Should().BeEmpty();
        plan.OldVersion.Kept.Should().Contain(k => k.Reason.Contains("is not finished"), "the old-only archive is kept for the other loadout's undo");
        WabbajackModlistLoadoutGroup.Load(Connection.Db, otherGroup.Id).IsValid().Should().BeTrue("deleting the old file would have deleted the other loadout's group with it");
        WabbajackModlistLibraryFile.Load(Connection.Db, installed.Id).IsValid().Should().BeTrue();
        LibraryFile.Load(Connection.Db, oldOnlyLibraryId).IsValid().Should().BeTrue();
        Updater.RestorePointOf(otherUpdated).Should().NotBeNull("the other loadout can still undo");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, other.LoadoutId).Should().ContainSingle();
    }

    [Fact]
    public async Task RemovingTheOldFileFromTheLibraryIsRefusedWhileAnUpdateAwayFromItIsUnfinished()
    {
        var (_, one, two, group) = await InstalledVersionOne();
        var updated = await ApplyUpdate(group, two);
        var planner = ServiceProvider.GetRequiredService<ModlistRemovalPlanner>();
        var plan = planner.Plan(LibraryFileId.From(one.Id));

        var refused = await Record.ExceptionAsync(() => planner.RemoveFromLibrary(plan, deleteArchives: true));

        plan.UnfinishedUpdates.Should().ContainSingle();
        plan.Groups.Should().BeEmpty("the group is on the new file");
        refused.Should().BeOfType<ModlistRemovalRefusedException>();
        WabbajackModlistLoadoutGroup.Load(Connection.Db, updated.Id).IsValid().Should().BeTrue();
        WabbajackModlistLibraryFile.Load(Connection.Db, one.Id).IsValid().Should().BeTrue();

        await Finisher.Finish(updated, deleteOldArchives: false, CancellationToken.None);
        await planner.RemoveFromLibrary(planner.Plan(LibraryFileId.From(one.Id)), deleteArchives: true);

        WabbajackModlistLibraryFile.Load(Connection.Db, one.Id).IsValid().Should().BeFalse("once the update is finished the old file is only a library file");
        WabbajackModlistLoadoutGroup.Load(Connection.Db, updated.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task FinishDropsTheProtectionOfEveryEarlierUnfinishedUpdateAndGarbageCollectionTakesTheirBytes()
    {
        var basis = Bytes(60, 200);
        var archive = await AddZip("base.zip", new Dictionary<string, byte[]> { ["base.bin"] = basis });
        async Task<WabbajackModlistLibraryFile.ReadOnly> Version(int seed)
        {
            var builder = new ListBuilder();
            builder.Archives.Add(archive);
            builder.Directives.Add(builder.Patched("mods/A/p.bin", archive, "base.bin", basis, [.. basis, .. Bytes(seed, 30)]));
            builder.Directives.Add(builder.Inline("profiles/Default/modlist.txt", "+A\n"));
            return await AddModlist(builder.ToList("Test list"), builder.Blobs);
        }
        var one = await Version(61);
        var two = await Version(62);
        var three = await Version(63);
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, one, Options);
        var oneHashes = HashesOf(group).ToArray();
        var afterTwo = await ApplyUpdate(group, two);
        var twoHashes = HashesOf(afterTwo).ToArray();
        var afterThree = await ApplyUpdate(Fresh(group), three);
        var gone = oneHashes.Union(twoHashes).Except(HashesOf(afterThree)).ToArray();
        gone.Should().NotBeEmpty();
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().HaveCount(2);
        var collector = ServiceProvider.GetRequiredService<IGarbageCollectorRunner>();
        await collector.RunAsync();
        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeTrue("both restore points are protected");

        await Finisher.Finish(afterThree, deleteOldArchives: false, CancellationToken.None);
        await collector.RunAsync();

        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty("the first update's row is not reachable from the group once the second overwrote its restore point");
        foreach (var hash in gone) (await HaveInStore(hash)).Should().BeFalse();
        foreach (var hash in HashesOf(Fresh(group))) (await HaveInStore(hash)).Should().BeTrue();
    }

    [Fact]
    public async Task CancellingApplyStopsTheInstallJobAndLeavesTheGroupMidUpdate()
    {
        var archive = await AddZip("nested.zip", new Dictionary<string, byte[]> { ["textures.bsa"] = "BSA\0not really"u8.ToArray() });
        var two = new ListBuilder();
        two.Archives.Add(archive);
        two.Directives.Add(new FromArchive(P("mods/A/a.dds"), Xx64.Of(HoldingOpener.Content), HoldingOpener.Content.Length, new ArchiveHashPath(archive.XxHash64, [P("textures.bsa"), P("a.dds")])));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var oneList = await AddList("+A\n", FilesOf("A"));
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, oneList, Options);
        _opener.Armed = true;
        using var cancel = new CancellationTokenSource();

        var apply = Updater.Apply(group, newFile, false, NoKeptGameFiles, cancel.Token);
        await _opener.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        var stopped = await Record.ExceptionAsync(() => apply.WaitAsync(TimeSpan.FromSeconds(30)));

        stopped.Should().BeAssignableTo<OperationCanceledException>("cancelling ends the wait and stops the job");
        ModlistInstallRuns.IsMidUpdate(Fresh(group)).Should().BeTrue("the switch was committed before the job stopped, which is the state Resume and Undo start from");
        Updater.RestorePointOf(Fresh(group)).Should().NotBeNull();
    }

    private readonly HoldingOpener _opener = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var configured = base.AddServices(services);
        // The first opener that can open a path is used, so this one goes ahead of the real one.
        configured.Insert(0, ServiceDescriptor.Singleton<INestedArchiveOpener>(_opener));
        return configured;
    }

    /// <summary>Opens nothing until armed; then every open of a .bsa member says it arrived and waits for the job to be stopped.</summary>
    private sealed class HoldingOpener : INestedArchiveOpener
    {
        public static readonly byte[] Content = Enumerable.Range(0, 4000).Select(i => (byte)(i * 7)).ToArray();

        public volatile bool Armed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanOpen(RelativePath name) => Armed && name.Extension == new Extension(".bsa");

        public async Task<Stream?> Open(Stream archive, RelativePath member, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task FinishUpdateKeepsAnOldFileAnotherLoadoutStillInstalls()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        var other = await CreateLoadout();
        await Install(other, one, Options);
        var updated = await ApplyUpdate(group, two);

        var plan = Finisher.Plan(updated);
        await Finisher.Finish(updated, deleteOldArchives: true, CancellationToken.None);

        plan.OldVersion.Groups.Should().ContainSingle();
        plan.OldVersion.OwnedArchives.Should().BeEmpty();
        WabbajackModlistLibraryFile.Load(Connection.Db, one.Id).IsValid().Should().BeTrue("the other loadout's group still reads from it");
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task TheOldFileIsNoLongerPendingAfterTheSwitchAndTheNewFileIsNotPendingBeforeIt()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        one = await WithIdentity(Connection, one, "a-list", "wabbajack-tools/mod-lists");
        two = await WithIdentity(Connection, two, "a-list", "wabbajack-tools/mod-lists");
        var elsewhere = await WithIdentity(Connection, await AddList("+C\n", FilesOf("C")), "another-list", "wabbajack-tools/mod-lists");
        var sameMachineUrlInAnotherRepository = await WithIdentity(Connection, await AddList("+D\n", FilesOf("D")), "a-list", "someone/else");

        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, one, loadout.LoadoutId).Should().BeFalse("it is the installed version");
        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, two, loadout.LoadoutId).Should().BeTrue("installing it is an update of the installed list, not an install");
        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, elsewhere, loadout.LoadoutId).Should().BeFalse();
        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, sameMachineUrlInAnotherRepository, loadout.LoadoutId).Should().BeFalse("another repository's list is another list");

        var updated = await ApplyUpdate(group, two);

        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, WabbajackModlistLibraryFile.Load(Connection.Db, one.Id), loadout.LoadoutId).Should().BeTrue("the old version is superseded");
        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, WabbajackModlistLibraryFile.Load(Connection.Db, two.Id), loadout.LoadoutId).Should().BeFalse("it is the installed version now");
        updated.ModlistId.Value.Should().Be(two.Id);
    }

    [Fact]
    public async Task AnOldFileWithoutIdentityIsSupersededOnceItIsTheGroupsPreviousList()
    {
        var (loadout, one, two, group) = await InstalledVersionOne();
        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, two, loadout.LoadoutId).Should().BeFalse("without an identity nothing says the two are versions of one list");

        await ApplyUpdate(group, two);

        ModlistUpdater.IsSupersededOrPendingUpdate(Connection.Db, WabbajackModlistLibraryFile.Load(Connection.Db, one.Id), loadout.LoadoutId).Should().BeTrue();
    }

    [Fact]
    public async Task IsDownloadedIsFalseWhileAnArchiveIsMissingAndTrueOnceTheLibraryHoldsIt()
    {
        var content = Bytes(40);
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("l.bin")] = content });
        var bytes = await File.ReadAllBytesAsync(zip.Path.ToString());
        var late = new ArchiveEntry(Xx64.Of(bytes), bytes.Length, "late.zip", "", new HttpState(new Uri("https://example.invalid/late.zip"), []));
        var list = new ListBuilder();
        list.Archives.Add(late);
        list.Directives.Add(list.Member("mods/L/l.bin", late, "l.bin", content));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+L\n"));
        var newFile = await AddModlist(list.ToList("Test list"), list.Blobs);
        var loadout = await CreateLoadout();
        (await Updater.IsDownloaded(newFile, loadout.LoadoutId, CancellationToken.None)).Should().BeFalse();

        // The library gets the archive only after the first look.
        await RegisterLocalArchive(zip.Path);

        (await Updater.IsDownloaded(newFile, loadout.LoadoutId, CancellationToken.None)).Should().BeTrue("the library holds it now");
    }

    [Fact]
    public async Task IsDownloadedIsTrueForAListWithNothingToFetch()
    {
        var (loadout, _, two, _) = await InstalledVersionOne();

        (await Updater.IsDownloaded(two, loadout.LoadoutId, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task DownloadReturnsWhenThereIsNothingToFetch()
    {
        var (loadout, _, two, _) = await InstalledVersionOne();

        var download = Updater.Download(two, loadout.LoadoutId, CancellationToken.None);

        await download.WaitAsync(TimeSpan.FromSeconds(30));
        (await Updater.IsDownloaded(two, loadout.LoadoutId, CancellationToken.None)).Should().BeTrue();
    }
}
