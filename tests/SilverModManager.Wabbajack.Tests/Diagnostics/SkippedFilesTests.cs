using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Diagnostics;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Diagnostics;

public class SkippedFilesTests(ITestOutputHelper helper) : InstallTestBase<SkippedFilesTests>(helper)
{
    [Fact]
    public async Task AGroupWithSkipRowsYieldsOneWarningWithTheCountAndThePaths()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout, "List");
        await AddSkips(group, "Mods/A/a.esp", "Mods/B/b.esp", "Mods/C/c.esp");

        var diagnostics = await Diagnose(loadout);

        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var text = Text(diagnostic);
        text.Should().Contain("3");
        text.Should().Contain("Mods/A/a.esp").And.Contain("Mods/B/b.esp").And.Contain("Mods/C/c.esp");
    }

    [Fact]
    public async Task OnlyTheFirstTenPathsAreListed()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout, "List");
        await AddSkips(group, Enumerable.Range(0, 12).Select(i => $"Mods/file{i:00}.esp").ToArray());

        var text = Text((await Diagnose(loadout)).Should().ContainSingle().Subject);

        text.Should().Contain("12");
        text.Should().Contain("file00.esp").And.Contain("file09.esp");
        text.Should().NotContain("file10.esp").And.NotContain("file11.esp");
    }

    [Fact]
    public async Task AGroupWithoutSkipRowsYieldsNothing()
    {
        var loadout = await CreateLoadout();
        await AddGroup(loadout, "List");

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoGroupsYieldTwoWarnings()
    {
        var loadout = await CreateLoadout();
        var first = await AddGroup(loadout, "First");
        var second = await AddGroup(loadout, "Second");
        await AddSkips(first, "one.esp");
        await AddSkips(second, "two.esp");

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().HaveCount(2);
        diagnostics.Select(Text).Should().Contain(t => t.Contains("one.esp")).And.Contain(t => t.Contains("two.esp"));
    }

    [Fact]
    public async Task RowsOfARetractedGroupAreIgnored()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout, "List");
        await AddSkips(group, "gone.esp");
        // The host started the watcher; with it stopped the rows outlive the group, as after a crash mid-cleanup.
        await Watcher().StopAsync(CancellationToken.None);
        await RetractGroup(group);

        WabbajackSkippedFile.FindByGroup(Connection.Db, group.Id).Should().NotBeEmpty();
        (await Diagnose(Loadout.Load(Connection.Db, loadout.Id))).Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingTheCollectionLeavesNoSkipRowsAndNoWarning()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout, "List");
        await AddSkips(group, "gone.esp");

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(group.Id));

        await WaitUntil(() => WabbajackSkippedFile.FindByGroup(Connection.Db, group.Id).Count == 0);
        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheWatcherRemovesTheSkipRowsOfAGroupRetractedWhileItRunsAndNothingElse()
    {
        var loadout = await CreateLoadout();
        var removed = await AddGroup(loadout, "Removed");
        var kept = await AddGroup(loadout, "Kept");
        await AddSkips(removed, "a.esp", "b.esp");
        await AddSkips(kept, "c.esp");

        await RetractGroup(removed);
        await WaitUntil(() => WabbajackSkippedFile.FindByGroup(Connection.Db, removed.Id).Count == 0);

        WabbajackSkippedFile.FindByGroup(Connection.Db, kept.Id).Should().ContainSingle();
        var texts = (await Diagnose(Loadout.Load(Connection.Db, loadout.Id))).Select(Text).ToArray();
        texts.Should().ContainSingle().Which.Should().Contain("c.esp");
    }

    [Fact]
    public async Task TheWatcherKeepsTheSkipRowsOfAGroupWhoseModlistIsReplaced()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout, "Kept");
        var removed = await AddGroup(loadout, "Removed");
        await AddSkips(group, "kept.esp");
        await AddSkips(removed, "gone.esp");
        var other = await AddModlist(new ListBuilder().ToList("Other"), new Dictionary<string, byte[]>());

        // Replacing the reference retracts the old value while the group itself stays.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group.Id, WabbajackModlistLoadoutGroup.Modlist, other.Id);
            await tx.Commit();
        }
        // Cleanups run in order, so once this group's rows are gone the replacement has been looked at too.
        await RetractGroup(removed);
        await WaitUntil(() => WabbajackSkippedFile.FindByGroup(Connection.Db, removed.Id).Count == 0);

        WabbajackSkippedFile.FindByGroup(Connection.Db, group.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task TheWatcherRefusesASecondStart()
    {
        var start = () => Watcher().StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UndoingTheRemovalOfAnInstalledListBringsBackItsSkipRows()
    {
        var archive = await AddZip("a.zip", new() { ["base.bin"] = [1, 2, 3], ["tex.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(UnwritableTexture("mods/A/patched.bin", archive, "tex.dds"));
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, await AddModlist(list.ToList(), list.Blobs), new InstallOptions(false, new HashSet<RelativePath>()));
        SkipsOf(group).Should().ContainSingle();
        await StampRevision(loadout);
        var restorePoint = UndoService.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.TxEntity).Last().Revision;

        await ServiceProvider.GetRequiredService<ILoadoutManager>().RemoveCollection(CollectionGroupId.From(group.Id));
        WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).IsValid().Should().BeFalse();
        await UndoService.RevertTo(restorePoint);

        WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).IsValid().Should().BeTrue();
        SkipsOf(group).Should().ContainSingle(s => s.Kind == SkipKind.Transform, "the restored list is still missing the file it skipped");
        (await Diagnose(Loadout.Load(Connection.Db, loadout.Id))).Should().ContainSingle();
    }

    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    private async Task StampRevision(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.LastAppliedDateTime, DateTime.UtcNow);
        await tx.Commit();
    }

    private WabbajackModlistRemovalWatcher Watcher()
        => ServiceProvider.GetServices<IHostedService>().OfType<WabbajackModlistRemovalWatcher>().Single();

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly stale)
    {
        var loadout = Loadout.Load(Connection.Db, stale.Id);
        var emitter = ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<WabbajackSkippedFilesEmitter>().Single();
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) result.Add(diagnostic);
        return result.ToArray();
    }

    private static string Text(Diagnostic diagnostic)
    {
        var formatter = new PlainWriter();
        return diagnostic.FormatSummary(formatter) + "\n" + diagnostic.FormatDetails(formatter);
    }

    /// <summary>Removes the group's entity but not the rows that point at it, which is what a watcher exists to clean up after.</summary>
    private async Task RetractGroup(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        using var tx = Connection.BeginTransaction();
        tx.Delete(group.Id, recursive: false);
        await tx.Commit();
    }

    private async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddGroup(Loadout.ReadOnly loadout, string name)
    {
        var modlist = await AddModlist(new ListBuilder().ToList(name), new Dictionary<string, byte[]>());
        using var tx = Connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = modlist.Id,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId, IsDisabled = true },
                },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }

    private async Task AddSkips(WabbajackModlistLoadoutGroup.ReadOnly group, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var path in paths)
        {
            _ = new WabbajackSkippedFile.New(tx, out var id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId, Name = path },
                GroupId = WabbajackModlistLoadoutGroupId.From(group.Id),
                To = path,
                Reason = "test",
                Kind = SkipKind.MissingArchive,
            };
        }
        await tx.Commit();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(25);
        }
    }

    private sealed class PlainWriter : IDiagnosticWriter
    {
        public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull => Write(ref state, value.ToString().AsSpan());

        public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct => Write(ref state, value.ToString().AsSpan());

        public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value) => state.StringBuilder.Append(value);
    }
}
