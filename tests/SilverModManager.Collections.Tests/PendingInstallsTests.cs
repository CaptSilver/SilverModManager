using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Games.FOMOD;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.Collections.Tests;

public class PendingInstallsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<PendingInstallsTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private OfflineCollections Offline() => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    private async Task<(EntityId Group, CollectionDownload.ReadOnly[] Items)> InstallTwoMods()
    {
        var mods = Enumerable.Range(0, 2).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await Offline().Create(mods);
        var loadout = await CreateLoadout();
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await Await(job).WaitAsync(Timeout);
        return (group.Id, items);
    }

    private static async Task<NexusCollectionLoadoutGroup.ReadOnly> Await(IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> job) => await job;

    private static CollectionRoot OtherRevisionManifest() => new()
    {
        Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
        Mods =
        [
            new Mod
            {
                Name = "Other revision mod",
                DomainName = GameDomain.From("cyberpunk2077"),
                Source = new ModSource
                {
                    Type = ModSourceType.Direct,
                    Md5 = Md5Value.From((UInt128)777),
                    Url = new Uri("https://example.invalid/other.zip"),
                    LogicalFilename = "other.zip",
                    FileSize = Size.From(1UL),
                },
            },
        ],
    };

    private static CollectionDownloadId IdOf(CollectionDownload.ReadOnly download) => CollectionDownloadId.From(download.Id);

    [Fact]
    public async Task Record_WritesOneRowWithKindAndReason()
    {
        var (group, items) = await InstallTwoMods();

        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "no match");

        var row = PendingInstalls.Find(Connection.Db, group, IdOf(items[0])).Value;
        row.Kind.Should().Be(ReviewKind.NoInstaller);
        row.Reason.Value.Should().Be("no match");
        NexusCollectionPendingInstall.Dismissed.TryGetValue(row, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Record_Twice_LeavesOneRow()
    {
        var (group, items) = await InstallTwoMods();

        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "first");
        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NeedsChoices, "second");

        var rows = PendingInstalls.For(Connection.Db, group);
        rows.Should().ContainSingle();
        rows[0].Kind.Should().Be(ReviewKind.NeedsChoices);
        rows[0].Reason.Value.Should().Be("second");
    }

    [Fact]
    public async Task Record_CopiesTheReplacementStamp_ButNotDismissed()
    {
        var (group, items) = await InstallTwoMods();
        var first = await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "first");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(first.Id, NexusCollectionPendingInstall.ReplacesGroup, 42UL);
            tx.Add(first.Id, NexusCollectionPendingInstall.ReplacedWasDisabled, true);
            tx.Add(first.Id, NexusCollectionPendingInstall.Dismissed, true);
            await tx.Commit();
        }

        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "again");

        var row = PendingInstalls.Find(Connection.Db, group, IdOf(items[0])).Value;
        NexusCollectionPendingInstall.ReplacesGroup.TryGetValue(row, out var replaces).Should().BeTrue();
        replaces.Should().Be(42UL);
        NexusCollectionPendingInstall.ReplacedWasDisabled.TryGetValue(row, out var wasDisabled).Should().BeTrue();
        wasDisabled.Should().BeTrue();
        PendingInstalls.IsDismissed(row).Should().BeFalse();
    }

    [Fact]
    public async Task Retract_RemovesEveryRowForThePair()
    {
        var (group, items) = await InstallTwoMods();
        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "a");
        await PendingInstalls.Record(Connection, group, items[1], ReviewKind.NoInstaller, "b");

        using (var tx = Connection.BeginTransaction())
        {
            PendingInstalls.Retract(tx, Connection.Db, group, IdOf(items[0]));
            await tx.Commit();
        }

        PendingInstalls.Find(Connection.Db, group, IdOf(items[0])).HasValue.Should().BeFalse();
        PendingInstalls.Find(Connection.Db, group, IdOf(items[1])).HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task For_IsDistinctByDownload_AndIgnoresOtherRevisions()
    {
        var (group, items) = await InstallTwoMods();
        var (_, _, otherItems) = await Offline().Create(OtherRevisionManifest(), revisionNumber: 2);
        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "mine");

        // Written by hand: Record on the foreign download is fine too, but this is the shape an update leaves behind.
        using (var tx = Connection.BeginTransaction())
        {
            _ = new NexusCollectionPendingInstall.New(tx)
            {
                CollectionGroupId = NexusCollectionLoadoutGroupId.From(group),
                DownloadId = IdOf(otherItems[0]),
                Kind = ReviewKind.NoInstaller,
                Reason = "foreign",
            };
            // A second row for the same download, as two overlapping Record calls could leave.
            _ = new NexusCollectionPendingInstall.New(tx)
            {
                CollectionGroupId = NexusCollectionLoadoutGroupId.From(group),
                DownloadId = IdOf(items[0]),
                Kind = ReviewKind.NoInstaller,
                Reason = "duplicate",
            };
            await tx.Commit();
        }

        var rows = PendingInstalls.For(Connection.Db, group);
        rows.Should().ContainSingle();
        rows[0].DownloadId.Should().Be(IdOf(items[0]));
    }

    [Fact]
    public async Task Observe_EmitsOnRecordAndRetract()
    {
        var (group, items) = await InstallTwoMods();
        var lengths = new List<int>();
        using var subscription = PendingInstalls.Observe(Connection, group).Subscribe(rows => { lock (lengths) lengths.Add(rows.Length); });

        await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "a");
        using (var tx = Connection.BeginTransaction())
        {
            PendingInstalls.Retract(tx, Connection.Db, group, IdOf(items[0]));
            await tx.Commit();
        }

        lock (lengths)
        {
            lengths.Should().StartWith([0, 1]);
            lengths.Last().Should().Be(0);
        }
    }

    [Fact]
    public async Task Observe_EmitsWhenARowIsDismissed()
    {
        var (group, items) = await InstallTwoMods();
        var row = await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "a");
        var emissions = new List<bool>();
        using var subscription = PendingInstalls.Observe(Connection, group)
            .Subscribe(rows => { lock (emissions) emissions.Add(rows.Single().IsDismissedRow()); });

        await PendingInstalls.Dismiss(Connection, row.Id);

        lock (emissions)
        {
            emissions.Should().StartWith(false);
            emissions.Last().Should().BeTrue();
        }
    }

    [Fact]
    public async Task Dismiss_SetsTheFlag_AndIsANoOpOnADeadRow()
    {
        var (group, items) = await InstallTwoMods();
        var row = await PendingInstalls.Record(Connection, group, items[0], ReviewKind.NoInstaller, "a");

        await PendingInstalls.Dismiss(Connection, row.Id);
        PendingInstalls.IsDismissed(PendingInstalls.Find(Connection.Db, group, IdOf(items[0])).Value).Should().BeTrue();

        using (var tx = Connection.BeginTransaction())
        {
            PendingInstalls.Retract(tx, Connection.Db, group, IdOf(items[0]));
            await tx.Commit();
        }

        await PendingInstalls.Dismiss(Connection, row.Id);

        PendingInstalls.Find(Connection.Db, group, IdOf(items[0])).HasValue.Should().BeFalse();
        Connection.Db.Datoms(row.Id).Any(datom => datom.A == Connection.AttributeCache.GetAttributeId(NexusCollectionPendingInstall.Dismissed.Id)).Should().BeFalse();
    }

    [Fact]
    public void ReviewRequiredException_IsUserFacing_WithTheFixedMessages()
    {
        var inner = new InvalidOperationException("inner");
        var noInstaller = new ReviewRequiredException(ReviewKind.NoInstaller, "Cool Mod", "no match", inner);
        var choices = new ReviewRequiredException(ReviewKind.NeedsChoices, "Cool Mod", "");

        noInstaller.Message.Should().Be("No installer knows where the files of `Cool Mod` go.");
        choices.Message.Should().Be("`Cool Mod` has choices to make before it can be installed.");
        noInstaller.Title.Should().Be("Needs review");
        noInstaller.Kind.Should().Be(ReviewKind.NoInstaller);
        noInstaller.Reason.Should().Be("no match");
        noInstaller.InnerException.Should().BeSameAs(inner);
        choices.Reason.Should().BeEmpty();
    }
}

internal static class PendingRowExtensions
{
    public static bool IsDismissedRow(this NexusCollectionPendingInstall.ReadOnly row) => PendingInstalls.IsDismissed(row);
}
