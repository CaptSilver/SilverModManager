using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Update;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>
/// The pass that gives the mod groups of lists installed before links were recorded their archive and link. Every test
/// installs a list normally and then removes what the install wrote, which is the state those installs are in.
/// </summary>
public class ModGroupLinkBackfillTests(ITestOutputHelper helper) : InstallTestBase<ModGroupLinkBackfillTests>(helper)
{
    private static readonly byte[] Member = "member"u8.ToArray();
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private ILibraryArchiveLocator Locator => ServiceProvider.GetRequiredService<ILibraryArchiveLocator>();

    private ModlistInstallRuns Runs => ServiceProvider.GetRequiredService<ModlistInstallRuns>();

    private ModGroupLinkBackfill Backfill() => new(Connection, FileStore, Locator, Runs, NullLogger<ModGroupLinkBackfill>.Instance);

    private async Task<(ArchiveEntry Entry, TemporaryPath Zip, EntityId File)> AddKeptZip(string name, byte[] member)
    {
        var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [P("f.bin")] = member });
        var file = (await RegisterLocalArchive(zip.Path)).AsLibraryFile();
        return (new ArchiveEntry(file.XxHash64Identity()!.Value, (long)file.Size.Value, name, "", new HttpState(new Uri($"https://example.invalid/{name}"), [])), zip, file.Id);
    }

    private async Task<WabbajackModlistLibraryFile.ReadOnly> AddList(string name, ArchiveEntry archive, byte[] member, bool second = false)
    {
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/f.bin", archive, "f.bin", member));
        if (second) list.Directives.Add(list.Inline("mods/B/b.txt", "generated"));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", second ? "+B\n+A\n" : "+A\n"));
        return await AddModlist(list.ToList(name), list.Blobs);
    }

    private IEnumerable<WabbajackModGroup.ReadOnly> ModGroups(EntityId group)
        => LoadoutItem.FindByParent(Connection.Db, group).OfTypeLoadoutItemGroup()
            .Select(g => WabbajackModGroup.Load(Connection.Db, g.Id)).Where(g => g.IsValid());

    private WabbajackModGroup.ReadOnly ModA(EntityId group) => ModGroups(group).Single(g => g.Folder == "A");

    private (FileIdentity? Main, EntityId? Link) StateOf(EntityId group)
    {
        var mod = ModA(group);
        var link = LibraryLinkedLoadoutItem.Load(Connection.Db, mod.Id);
        return (WabbajackModGroup.MainArchive.TryGetValue(mod, out var main) ? main : null, link.IsValid() ? link.LibraryItem.Id : null);
    }

    /// <summary>What an install made before links existed looks like: no recorded archive and no link on any mod group.</summary>
    private async Task StripLinks(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var mod in ModGroups(group.Id))
        {
            tx.RetractCurrent(mod.Id, WabbajackModGroup.MainArchive);
            tx.RetractCurrent(mod.Id, LibraryLinkedLoadoutItem.LibraryItem);
        }
        await tx.Commit();
    }

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLoadoutGroup.ReadOnly Group, ArchiveEntry Archive, EntityId File)> StrippedInstall(string name = "Test list")
    {
        var archive = await AddZip($"{name}.zip", new() { ["f.bin"] = Member });
        var loadout = await CreateLoadout();
        var modlist = await AddList(name, archive, Member);
        var (_, group) = await Install(loadout, modlist, Options);
        await StripLinks(group);
        StateOf(group.Id).Should().Be((null, null), "the fixture starts from an install that has neither");
        return (loadout, group, archive, Locator.FindTopLevelByXxHash64(Connection.Db, archive.XxHash64)!.Value.Id);
    }

    [Fact]
    public async Task AnInstallMadeBeforeLinksWereRecordedIsLinkedOnce()
    {
        var (_, group, archive, file) = await StrippedInstall();

        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Should().Be((archive.XxHash64, file));
        var basis = Connection.Db.BasisTxId;
        await Backfill().Run(TestContext.Current.CancellationToken);
        Connection.Db.BasisTxId.Should().Be(basis, "a second run has nothing to write");
    }

    [Fact]
    public async Task ARemovedArchiveUnlinksTheGroupAndAReAddedOneRelinksIt()
    {
        var member = "kept"u8.ToArray();
        var (entry, zip, file) = await AddKeptZip("kept.zip", member);
        await using var _ = zip;
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, await AddList("Kept list", entry, member), Options);
        await StripLinks(group);
        await Backfill().Run(TestContext.Current.CancellationToken);
        StateOf(group.Id).Link.Should().Be(file);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(file, recursive: false);
            await tx.Commit();
        }
        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Should().Be((entry.XxHash64, null), "the group keeps the archive it was built from and loses a link to nothing");

        var added = (await RegisterLocalArchive(zip.Path)).AsLibraryFile();
        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Link.Should().Be(added.Id);
    }

    [Fact]
    public async Task TwoLibraryCopiesOfOneArchiveDoNotFlipTheLink()
    {
        var (_, group, archive, first) = await StrippedInstall();
        EntityId second;
        using (var tx = Connection.BeginTransaction())
        {
            var bytes = "second copy"u8.ToArray();
            var copy = new LibraryFile.New(tx, out var id)
            {
                FileName = "copy.zip",
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "copy.zip" },
            };
            tx.Add(id, LibraryFile.XxHash64, archive.XxHash64.XxHash64Value());
            var result = await tx.Commit();
            second = result.Remap(copy).Id;
        }
        second.Should().NotBe(first);
        // The link names the second copy, which the locator would not return first.
        using (var tx = Connection.BeginTransaction())
        {
            var mod = ModA(group.Id).Id;
            tx.Add(mod, WabbajackModGroup.MainArchive, archive.XxHash64);
            tx.Add(mod, LibraryLinkedLoadoutItem.LibraryItem, second);
            await tx.Commit();
        }

        await Backfill().Run(TestContext.Current.CancellationToken);
        var basis = Connection.Db.BasisTxId;
        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Link.Should().Be(second);
        Connection.Db.BasisTxId.Should().Be(basis);
    }

    [Fact]
    public async Task AListWhoseInstallIsRunningIsLeftToTheInstall()
    {
        var (loadout, group, _, _) = await StrippedInstall();
        var gate = new TaskCompletionSource();
        var job = NewJob(loadout, WabbajackModlistLibraryFile.Load(Connection.Db, group.ModlistId.Value), Options);
        Runs.EnsureNoneInto(job.TargetLoadout);
        var running = ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job, async _ =>
        {
            await gate.Task;
            return default;
        });
        try
        {
            Runs.FindInto(loadout.LoadoutId).Should().NotBeNull();

            await Backfill().Run(TestContext.Current.CancellationToken);

            StateOf(group.Id).Should().Be((null, null));
        }
        finally
        {
            gate.SetResult();
            await running;
        }

        await Backfill().Run(TestContext.Current.CancellationToken);
        StateOf(group.Id).Link.Should().NotBeNull("once the install is over the list is linked");
    }

    [Fact]
    public async Task AnUnfinishedUpdateIsLeftToItsResume()
    {
        var loadout = await CreateLoadout();
        var archive = await AddZip("one.zip", new() { ["f.bin"] = Member });
        var one = await AddList("Update list", archive, Member);
        var two = await AddList("Update list", archive, Member, second: true);
        var (_, group) = await Install(loadout, one, Options);

        var failing = new UpdateJobTests.FailingAfter(FileStore, 0);
        var job = NewJob(loadout, two, new InstallOptions(false, new HashSet<RelativePath>(), group.WabbajackModlistLoadoutGroupId),
            writer: new GeneratedFileWriter(failing, TemporaryFileManager), parallelism: 1, commits: new CommitPolicy(2, long.MaxValue, TimeSpan.MaxValue));
        var stopped = await Record.ExceptionAsync(() => Run(job));
        ModlistInstallRuns.IsMidUpdate(WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id)).Should().BeTrue($"the update stopped ({stopped?.GetType().Name ?? "no exception"}) and waits for its resume");
        await StripLinks(group);

        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Should().Be((null, null));
    }

    [Fact]
    public async Task AnInstallOrUpdateStartedDuringTheClassifyIsNotOverwritten()
    {
        var (_, group, _, _) = await StrippedInstall();
        var other = Xx64.From(0x999);

        await Backfill().Run(TestContext.Current.CancellationToken, afterClassify: async () =>
        {
            using var tx = Connection.BeginTransaction();
            tx.Add(ModA(group.Id).Id, WabbajackModGroup.MainArchive, other);
            await tx.Commit();
        });

        StateOf(group.Id).Should().Be((other, null), "the backfill dropped what it classified, because the group changed under it");

        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Should().Be((other, null), "a plain run follows the recorded archive, which no library file has");
    }

    [Fact]
    public async Task AListWhoseGameIsMissingDoesNotStopTheOthers()
    {
        var (_, good, archive, file) = await StrippedInstall("Good list");

        // A group whose list is for a game this app has no title for, so classifying it throws.
        var brokenLoadout = await CreateLoadout();
        var builder = new ListBuilder();
        builder.Archives.Add(archive);
        builder.Directives.Add(builder.Member("mods/A/f.bin", archive, "f.bin", Member));
        builder.Directives.Add(builder.Inline("profiles/Default/modlist.txt", "+A\n"));
        var broken = await AddModlist(builder.ToList("Broken list") with { GameType = "NoSuchGame" }, builder.Blobs);
        EntityId brokenGroup;
        using (var tx = Connection.BeginTransaction())
        {
            var made = new WabbajackModlistLoadoutGroup.New(tx, out var id)
            {
                ModlistId = broken.Id,
                CollectionGroup = new CollectionGroup.New(tx, id)
                {
                    IsReadOnly = false,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Broken list", LoadoutId = brokenLoadout.LoadoutId },
                    },
                },
            };
            var mod = new WabbajackModGroup.New(tx, out var modId)
            {
                Folder = "A",
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, modId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, modId) { Name = "A", LoadoutId = brokenLoadout.LoadoutId, ParentId = id },
                },
            };
            brokenGroup = (await tx.Commit()).Remap(made).Id;
        }

        var failure = await Record.ExceptionAsync(() => Backfill().Run(TestContext.Current.CancellationToken));

        failure.Should().BeNull("one list's failure is logged, not thrown");
        StateOf(good.Id).Should().Be((archive.XxHash64, file));
        StateOf(brokenGroup).Should().Be((null, null));
    }

    [Fact]
    public async Task ADisabledFinishedListIsLinkedToo()
    {
        var (_, group, archive, file) = await StrippedInstall();
        using (var tx = Connection.BeginTransaction())
        {
            tx.RetractCurrent(group.Id, WabbajackModlistLoadoutGroup.InstalledAt);
            tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        await Backfill().Run(TestContext.Current.CancellationToken);

        StateOf(group.Id).Should().Be((archive.XxHash64, file));
    }

    [Fact]
    public async Task AGroupWhoseLoadoutIsGoneIsSkippedAndTheOthersAreLinked()
    {
        var (goneLoadout, gone, _, _) = await StrippedInstall("Gone list");
        var (_, kept, archive, file) = await StrippedInstall("Kept list");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(goneLoadout.Id, recursive: false);
            await tx.Commit();
        }

        var failure = await Record.ExceptionAsync(() => Backfill().Run(TestContext.Current.CancellationToken));

        failure.Should().BeNull();
        StateOf(kept.Id).Should().Be((archive.XxHash64, file));
        gone.Id.Should().NotBe(kept.Id);
    }

    [Fact]
    public async Task TheRunnerLinksAfterStartAndLogsAFailure()
    {
        var (_, group, _, file) = await StrippedInstall();
        using (var runner = new ModGroupLinkBackfillRunner(Backfill(), NullLogger<ModGroupLinkBackfillRunner>.Instance))
        {
            await runner.StartAsync(TestContext.Current.CancellationToken);
            (await Eventually(() => StateOf(group.Id).Link == file)).Should().BeTrue();
            await runner.StopAsync(TestContext.Current.CancellationToken);
        }

        var connection = Substitute.For<IConnection>();
        connection.Db.Returns(_ => throw new InvalidOperationException("the database is gone"));
        var failing = new ModGroupLinkBackfill(connection, FileStore, Locator, new ModlistInstallRuns(Substitute.For<IJobMonitor>(), connection), NullLogger<ModGroupLinkBackfill>.Instance);
        var logger = new RecordingLogger<ModGroupLinkBackfillRunner>();
        using var failingRunner = new ModGroupLinkBackfillRunner(failing, logger);

        await failingRunner.StartAsync(TestContext.Current.CancellationToken);

        (await Eventually(() => logger.Warnings.Count > 0)).Should().BeTrue("a failed pass is logged");
        await failingRunner.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) lock (Warnings) Warnings.Add(formatter(state, exception));
        }
    }
}
