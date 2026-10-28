using System.Collections.Concurrent;
using System.Reactive.Linq;
using DynamicData;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Collections.Tests;

using Revision = (NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items);

/// <summary>
/// Updates of a collection whose mods come from Nexus Mods, the kind real collections are made of. Revision 1 lists u1, u2,
/// cf, cc, dr, opt (optional, not installed) and u3. Revision 2 lists them in another order with a new required mod in
/// front: nw, u3, cf with a new file, u1, cc with other fomod choices, opt, u2, and a new optional nwo; dr is gone.
/// A mod's file id is its mod id times ten plus one (cf's new file is 1013); nothing touches the network.
/// </summary>
public class CollectionUpdateNexusTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionUpdateNexusTests>(helper)
{
    private readonly ScriptedGuidedInstallerState _window = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddSingleton(_window)
            .AddTransient<IGuidedInstaller>(_ => _window.Create());
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    private const uint U1 = 1001, U2 = 1002, OldCf = 1003, NewCf = 1013, Cc = 1004, Dr = 1005, Opt = 1006, U3 = 1007, Nw = 1008, Nwo = 1009;

    private static FomodOption[] Choices(string firstStepOption) =>
    [
        new FomodOption { name = "Step 1", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = firstStepOption }] }] },
        new FomodOption { name = "Step 2", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option C" }] }] },
    ];

    /// <summary>A Nexus mod of the collection; with <paramref name="inLibrary"/> its archive is downloaded already.</summary>
    private async Task<Mod> NexusMod(string slot, uint modId, uint fileId, int archive, FomodOption[]? choices = null, bool inLibrary = true)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, _) = await Offline.CreateFomodMod(folder.Path, archive, plain: choices is null);
        if (inLibrary) await Offline.AddNexusDownload(path, modId, fileId);
        return new Mod
        {
            Name = $"Mod {slot}",
            DomainName = GameDomain.From("cyberpunk2077"),
            Source = new ModSource
            {
                Type = ModSourceType.NexusMods,
                ModId = ModId.From(modId),
                FileId = FileId.From(fileId),
                LogicalFilename = $"{slot}-{fileId}.zip",
                FileSize = Size.From((ulong)path.FileInfo.Size),
                Tag = slot,
            },
            Choices = choices is null ? null : new Choices { Type = ChoicesType.fomod, Options = choices },
        };
    }

    private static ModRule Over(string winner, string loser) => new()
    {
        Type = VortexModRuleType.After,
        Source = new VortexModReference { Tag = winner },
        Other = new VortexModReference { Tag = loser },
    };

    private async Task<Revision> Publish(int revisionNumber, Mod[] mods, int[] optional, ModRule[]? rules = null)
    {
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods,
            ModRules = rules ?? [],
        };
        var (file, revision, _) = await Offline.Create(manifest, optional, revisionNumber);
        return (file, revision, CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required));
    }

    private sealed record Scenario(Revision First, Revision Second, Mod[] SecondMods, LoadoutId Loadout, NexusCollectionLoadoutGroup.ReadOnly Group);

    private async Task<Scenario> InstalledFirstRevision()
    {
        var u1 = await NexusMod("u1", 100, U1, 0);
        var u2 = await NexusMod("u2", 101, U2, 1);
        var u3 = await NexusMod("u3", 102, U3, 6);
        var opt = await NexusMod("opt", 103, Opt, 5);
        var first = await Publish(1,
            [u1, u2, await NexusMod("cf", 104, OldCf, 2), await NexusMod("cc", 105, Cc, 3, Choices("Option A")), await NexusMod("dr", 106, Dr, 4), opt, u3],
            optional: [5]);
        Mod[] secondMods =
        [
            await NexusMod("nw", 107, Nw, 8), u3, await NexusMod("cf", 104, NewCf, 12), u1,
            await NexusMod("cc", 105, Cc, 3, Choices("Option B"), inLibrary: false), opt, u2, await NexusMod("nwo", 108, Nwo, 9),
        ];
        var second = await Publish(2, secondMods, optional: [5, 7], rules: [Over("u3", "u1")]);

        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        return new Scenario(first, second, secondMods, loadout, group);
    }

    private static async Task<NexusCollectionLoadoutGroup.ReadOnly> Await(IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> job)
    {
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    private NexusCollectionItemLoadoutGroup.ReadOnly[] Items(NexusCollectionLoadoutGroup.ReadOnly group) =>
        NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).GetCollectionItems().ToArray();

    /// <summary>The Nexus file id a loadout group was installed from.</summary>
    private uint InstalledFile(EntityId group)
    {
        var linked = LibraryLinkedLoadoutItem.Load(Connection.Db, group);
        return NexusModsLibraryItem.Load(Connection.Db, linked.LibraryItemId).FileMetadata.Uid.FileId.Value;
    }

    /// <summary>The Nexus file id the collection says a mod of it is.</summary>
    private static uint ListedFile(NexusCollectionItemLoadoutGroup.ReadOnly item) =>
        item.Download.TryGetAsCollectionDownloadNexusMods(out var nexus) ? nexus.FileUid.FileId.Value : 0;

    /// <summary>Every mod in the loadout installed from the Nexus file, wherever it sits.</summary>
    private LoadoutItemGroup.ReadOnly[] InstalledFrom(LoadoutId loadout, uint fileId) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup()
            .Where(group => LibraryLinkedLoadoutItem.Load(Connection.Db, group.Id).IsValid() && InstalledFile(group.Id) == fileId)
            .ToArray();

    private LoadoutItemGroup.ReadOnly Single(LoadoutId loadout, uint fileId) => InstalledFrom(loadout, fileId).Should().ContainSingle().Subject;

    private LoadoutItemGroup.ReadOnly Named(LoadoutId loadout, string name) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup().Single(group => group.AsLoadoutItem().Name == name);

    private LoadoutItemGroupId MyMods(LoadoutId loadout) => DefaultCollection.Of(Connection.Db, loadout) is { } myMods
        ? LoadoutItemGroupId.From(myMods.Value)
        : throw new InvalidOperationException("The loadout has no My Mods");

    /// <summary>Counts the collection installs of <paramref name="revision"/> that start from now on; the monitor forgets finished jobs.</summary>
    private sealed class InstallJobCounter : IDisposable
    {
        private readonly ConcurrentDictionary<JobId, byte> _started = new();
        private readonly IDisposable _subscription;

        public InstallJobCounter(IJobMonitor monitor, CollectionRevisionMetadata.ReadOnly revision)
        {
            _subscription = monitor.GetObservableChangeSet<InstallCollectionJob>().Subscribe(changes =>
            {
                foreach (var change in changes)
                {
                    if (change.Reason == ChangeReason.Add && change.Current.Definition is InstallCollectionJob install && install.RevisionMetadata.Id == revision.Id)
                        _started.TryAdd(change.Key, 0);
                }
            });
        }

        public int Count => _started.Count;

        public void Dispose() => _subscription.Dispose();
    }

    private InstallJobCounter CountInstallJobs(CollectionRevisionMetadata.ReadOnly revision) => new(ServiceProvider.GetRequiredService<IJobMonitor>(), revision);

    /// <summary>The collection holds exactly the required mods of <paramref name="revision"/> (revision 2's list), each installed from the file it lists.</summary>
    /// <param name="usersOwn">Mods that aren't this collection's (the user's own, another collection's), which may come from the same archives.</param>
    /// <param name="userRules">How many rules the user made, on top of the collection's one.</param>
    private void HoldsExactly(Scenario s, Revision revision, LoadoutItemGroupId[] usersOwn, int userRules = 0)
    {
        NexusCollectionLoadoutGroup.All(Connection.Db).Where(group => group.Collection.Id == s.Group.Collection.Id)
            .Should().ContainSingle("an update never adds a second collection");
        NexusCollectionLoadoutGroup.Load(Connection.Db, s.Group.Id).Revision.Id.Should().Be(revision.Revision.Id);

        var items = Items(s.Group);
        items.Select(item => item.Download.Name).Should().BeEquivalentTo(["Mod nw", "Mod u3", "Mod cf", "Mod u1", "Mod cc", "Mod u2"]);
        items.Should().OnlyContain(item => item.Download.CollectionRevision.Id == revision.Revision.Id, "nothing points at an older revision");
        items.Select(item => (Listed: ListedFile(item), Installed: InstalledFile(item.Id))).Should().OnlyContain(pair => pair.Listed == pair.Installed,
            "each mod is the one the revision lists, not another mod's entry");

        InstalledFrom(s.Loadout, OldCf).Where(group => usersOwn.Contains(group.LoadoutItemGroupId) is false).Should().BeEmpty("the old file of a changed mod goes once its replacement is installed");
        Single(s.Loadout, Cc).AsLoadoutItem().ParentId.Should().Be(LoadoutItemGroupId.From(s.Group.Id), "a mod whose choices changed is there once, in the collection");
        InstalledFrom(s.Loadout, Opt).Should().BeEmpty("an optional mod the user skipped stays skipped");
        InstalledFrom(s.Loadout, Nwo).Should().BeEmpty("a new optional mod isn't installed by an update");
        var rules = LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).ToArray();
        rules.Should().HaveCount(1 + userRules, "the collection's one rule and the user's own");
        rules.Select(rule => (InstalledFile(rule.SourceId), InstalledFile(rule.OtherId), rule.Kind)).Where(rule => rule.Equals((U3, U1, GroupRuleKind.After)))
            .Should().ContainSingle("the collection's rule is on the mods it names");
        CollectionDownloader.IsFullyInstalled(revision.Items, NexusCollectionLoadoutGroup.Load(Connection.Db, s.Group.Id).AsCollectionGroup(), Connection.Db)
            .Should().BeTrue("the revision's page must not offer to install the collection a second time");
    }

    [Fact]
    public async Task AnUpdateSortsEachNexusModByItsOwnFile()
    {
        var s = await InstalledFirstRevision();
        var u1Before = Single(s.Loadout, U1);
        using var installs = CountInstallJobs(s.Second.Revision);

        // What CollectionUpdateFlow.Run does for the page's Update button, with the user answering "Remove all".
        var result = await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        result.Error.Should().BeNull();
        result.Failed.Should().BeEmpty();
        HoldsExactly(s, s.Second, []);
        Single(s.Loadout, U1).Id.Should().Be(u1Before.Id, "an unchanged mod stays the same mod");
        Single(s.Loadout, Cc).Children.OfTypeLoadoutItemWithTargetPath().Select(file => file.TargetPath.Item3.ToString())
            .Should().Contain(path => path.Contains("mod3_b.out.esp"), "the new choices were installed");
        installs.Count.Should().Be(1);

        var dropped = Single(s.Loadout, Dr);
        result.Dropped.Select(mod => mod.Group).Should().Equal(dropped.LoadoutItemGroupId);
        dropped.AsLoadoutItem().ParentId.Should().Be(MyMods(s.Loadout), "a dropped mod waits in My Mods for the user's answer");

        await CollectionUpdater.RemoveDropped(ServiceProvider, result.Dropped);
        InstalledFrom(s.Loadout, Dr).Should().BeEmpty();
    }

    /// <summary>
    /// What an update did before Nexus mods were told apart: it pointed the installed mods at the new revision's
    /// downloads in list order, and the page's install then added the files that looked missing. dr keeps its old
    /// revision's download, as a mod that an update never reached would.
    /// </summary>
    private Task BreakTheWayTheEarlierUpdateDid(Scenario s) =>
        Break(s.Group, s.Loadout, s.Second, item => InstalledFile(item.Id) == Dr);

    /// <param name="untouched">Mods the switch leaves on their old revision's download.</param>
    private async Task Break(NexusCollectionLoadoutGroup.ReadOnly group, LoadoutId loadout, Revision to, Func<NexusCollectionItemLoadoutGroup.ReadOnly, bool> untouched)
    {
        var olds = Items(group).OrderBy(item => item.Download.ArrayIndex).ToArray();
        var news = to.Revision.Downloads.OrderBy(download => download.ArrayIndex).ToArray();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group.Id, NexusCollectionLoadoutGroup.Revision, to.Revision);
            tx.Add(group.Id, NexusCollectionLoadoutGroup.LibraryFile, to.File);
            for (var i = 0; i < Math.Min(olds.Length, news.Length); i++)
            {
                if (untouched(olds[i])) continue;
                tx.Add(olds[i].Id, NexusCollectionItemLoadoutGroup.Download, news[i]);
                tx.Add(olds[i].Id, NexusCollectionItemLoadoutGroup.IsRequired, news[i].IsRequired);
            }

            await tx.Commit();
        }

        await Await(InstallCollectionJob.Create(ServiceProvider, loadout, to.File, to.Revision, to.Items));
    }

    [Fact]
    public async Task RepairLeavesACollectionThatAnUpdateBrokeHoldingExactlyItsRevision()
    {
        var s = await InstalledFirstRevision();
        var cfOld = Single(s.Loadout, OldCf);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(cfOld.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        // The user's own mod from the same archive as the old cf, and one from dr's: neither belongs to the collection.
        var myMods = MyMods(s.Loadout);
        using (var tx = Connection.BeginTransaction())
        {
            ShareableMods.AddLinkedGroup(tx, s.Loadout, myMods, "My cf", LibraryLinkedLoadoutItem.Load(Connection.Db, cfOld.Id).LibraryItemId);
            ShareableMods.AddLinkedGroup(tx, s.Loadout, myMods, "My dr", LibraryLinkedLoadoutItem.Load(Connection.Db, Single(s.Loadout, Dr).Id).LibraryItemId);
            await tx.Commit();
        }

        var ownCf = Named(s.Loadout, "My cf").LoadoutItemGroupId;
        var ownDr = Named(s.Loadout, "My dr").LoadoutItemGroupId;
        (await LoadoutManager.AddGroupRule(s.Loadout, Single(s.Loadout, U1).LoadoutItemGroupId, cfOld.LoadoutItemGroupId, GroupRuleKind.After)).IsCycle.Should().BeFalse();

        await BreakTheWayTheEarlierUpdateDid(s);
        Items(s.Group).Length.Should().BeGreaterThan(s.Second.Items.Length, "the broken update left old versions in the collection");
        LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Select(rule => (InstalledFile(rule.SourceId), InstalledFile(rule.OtherId)))
            .Should().NotBeEmpty().And.NotContain((U3, U1), "the broken update applied the collection's rule to the wrong mods");

        var result = await CollectionUpdater.Repair(ServiceProvider, s.Group);

        HoldsExactly(s, s.Second, [ownCf, ownDr], userRules: 1);
        LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Select(rule => (InstalledFile(rule.SourceId), InstalledFile(rule.OtherId), rule.Kind))
            .Should().Contain((U1, NewCf, GroupRuleKind.After), "the user's rule on the old cf moved to its replacement");
        result.Removed.Should().ContainSingle("the old cf is the one old version with a replacement");
        var cc = Single(s.Loadout, Cc);
        result.SettingsChanged.Should().Equal([cc.AsLoadoutItem().Name], "cc kept revision 1's fomod choices, which the repair can only report");
        cc.Children.OfTypeLoadoutItemWithTargetPath().Select(file => file.TargetPath.Item3.ToString()).Should().Contain(path => path.Contains("mod3_a.out.esp"));
        Single(s.Loadout, NewCf).AsLoadoutItem().IsDisabled.Should().BeTrue("the user had switched the old cf off");
        result.Dropped.Should().ContainSingle();
        var dropped = InstalledFrom(s.Loadout, Dr).Single(group => group.LoadoutItemGroupId != ownDr);
        result.Dropped[0].Group.Should().Be(dropped.LoadoutItemGroupId);
        dropped.AsLoadoutItem().ParentId.Should().Be(myMods);
        LoadoutItem.Load(Connection.Db, ownCf).ParentId.Should().Be(myMods, "the user's own mods are never touched");
        LoadoutItem.Load(Connection.Db, ownDr).ParentId.Should().Be(myMods);

        (await CollectionUpdater.Repair(ServiceProvider, s.Group)).ChangedAnything.Should().BeFalse("a second run finds nothing to do");
    }

    /// <summary>Every loadout item with where it sits and which collection entry it is, to see that nothing moved.</summary>
    private (EntityId Item, EntityId Parent, EntityId Download, bool Disabled)[] Snapshot(LoadoutId loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout)
            .Select(item => (item.Id, LoadoutItem.ParentId.TryGetValue(item, out var parent) ? parent : default,
                NexusCollectionItemLoadoutGroup.Load(Connection.Db, item.Id) is var entry && entry.IsValid() ? entry.Download.Id : default,
                item.IsDisabled))
            .OrderBy(row => row.Id.Value)
            .ToArray();

    [Fact]
    public async Task ADryRunReportsTheRepairAndChangesNothing()
    {
        var s = await InstalledFirstRevision();
        await BreakTheWayTheEarlierUpdateDid(s);
        var before = Snapshot(s.Loadout);
        var rulesBefore = LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Select(rule => rule.Id).ToArray();

        var dryRun = await CollectionUpdater.Repair(ServiceProvider, s.Group, dryRun: true);

        Snapshot(s.Loadout).Should().Equal(before, "a dry run changes nothing");
        LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Select(rule => rule.Id).Should().Equal(rulesBefore);
        dryRun.DryRun.Should().BeTrue();
        dryRun.ChangedAnything.Should().BeTrue();

        var lines = dryRun.Describe("NCR").ToArray();
        lines[0].Should().Be("NCR (dry run, nothing changed):");
        lines.Should().Contain($"  {dryRun.Removed.Count} old versions would be removed (their replacement is installed)");
        lines.Should().Contain($"  {dryRun.Dropped.Count} mods would be moved to My Mods");
        lines.Should().Contain("    " + dryRun.Dropped[0].Name);
        lines.Should().Contain("  1 mods have changed install settings; reinstall them from the collection page");

        var real = await CollectionUpdater.Repair(ServiceProvider, s.Group);
        (real.Repointed, real.Removed, real.Dropped.Select(mod => mod.Name).ToArray(), real.SettingsChanged)
            .Should().BeEquivalentTo((dryRun.Repointed, dryRun.Removed, dryRun.Dropped.Select(mod => mod.Name).ToArray(), dryRun.SettingsChanged),
                "the dry run describes exactly what the repair then does");
    }

    [Fact]
    public async Task AModWithSeveralOldFilesAndOneReplacementGoesToMyModsWhole()
    {
        var main = await NexusMod("main", 200, 2001, 30);
        var extra = await NexusMod("extra", 200, 2002, 31);
        var first = await Publish(1, [main, extra], optional: []);
        var second = await Publish(2, [await NexusMod("main", 200, 2011, 32)], optional: []);
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        var oldMain = Single(loadout, 2001).LoadoutItemGroupId;
        var oldExtra = Single(loadout, 2002).LoadoutItemGroupId;
        await Break(group, loadout, second, _ => false);

        var result = await CollectionUpdater.Repair(ServiceProvider, group);

        result.Removed.Should().BeEmpty("which old file the new one replaces isn't certain");
        result.Dropped.Select(mod => mod.Group).Should().BeEquivalentTo([oldMain, oldExtra]);
        LoadoutItem.Load(Connection.Db, oldMain).ParentId.Should().Be(MyMods(loadout));
        LoadoutItem.Load(Connection.Db, oldExtra).ParentId.Should().Be(MyMods(loadout));
        Items(group).Select(item => ListedFile(item)).Should().Equal(2011u);
    }

    [Fact]
    public async Task WithoutAnOlderRevisionNothingIsRemoved()
    {
        var s = await InstalledFirstRevision();
        await BreakTheWayTheEarlierUpdateDid(s);
        await ServiceProvider.GetRequiredService<CollectionDownloader>().DeleteRevision(CollectionRevisionMetadataId.From(s.First.Revision.Id));
        var cfOld = Single(s.Loadout, OldCf).LoadoutItemGroupId;

        var result = await CollectionUpdater.Repair(ServiceProvider, s.Group);

        result.Removed.Should().BeEmpty("nothing proves the old cf is an older version of the collection's cf");
        result.Dropped.Select(mod => mod.Group).Should().Contain(cfOld);
        LoadoutItem.Load(Connection.Db, cfOld).ParentId.Should().Be(MyMods(s.Loadout));
        Items(s.Group).Select(item => item.Download.Name).Should().BeEquivalentTo(["Mod nw", "Mod u3", "Mod cf", "Mod u1", "Mod cc", "Mod u2"]);
    }

    /// <summary>A direct download of the collection, its archive in the library.</summary>
    private async Task<Mod> DirectMod(string slot, int archive)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, md5) = await Offline.CreateFomodMod(folder.Path, archive, plain: true);
        await Offline.AddDirectDownload(path, md5);
        return new Mod
        {
            Name = $"Mod {slot}",
            DomainName = GameDomain.From("cyberpunk2077"),
            Source = new ModSource
            {
                Type = ModSourceType.Direct,
                Md5 = md5,
                Url = new Uri($"https://example.invalid/{slot}.zip"),
                LogicalFilename = $"{slot}-{archive}.zip",
                FileSize = Size.From((ulong)path.FileInfo.Size),
                Tag = slot,
            },
        };
    }

    [Fact]
    public async Task AStaleBundledModIsKnownByItsOwnBundleAndAStaleDirectDownloadGoesToMyMods()
    {
        var bundled = new Mod { Name = "Mod bnd", DomainName = GameDomain.From("cyberpunk2077"), Source = new ModSource { Type = ModSourceType.Bundle, FileExpression = "bnd", LogicalFilename = "bnd" } };
        Task<(NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items)> PublishWith(int number, Mod direct, string content) =>
            Offline.Create(new CollectionRoot { Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") }, Mods = [direct, bundled] },
                null, number, new Dictionary<string, (string Path, string Content)[]> { ["bnd"] = [("bnd.txt", content)] });
        var first = await PublishWith(1, await DirectMod("x", 20), "old content");
        var second = await PublishWith(2, await DirectMod("x", 21), "new content");
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        var oldItems = Items(group).ToDictionary(item => item.Download.Name, item => item.AsLoadoutItemGroup().LoadoutItemGroupId);
        // Switched without repointing: both old mods still name revision 1's downloads, which name their mod truthfully,
        // but the repair must not lean on an entry it is checking.
        await Break(group, loadout, second, _ => true);

        var result = await CollectionUpdater.Repair(ServiceProvider, group);

        result.Removed.Should().ContainSingle("the bundle's own entry says which mod it is, and revision 1 listed its files");
        LoadoutItem.Load(Connection.Db, oldItems["Mod bnd"]).IsValid().Should().BeFalse();
        result.Dropped.Select(mod => mod.Group).Should().Equal([oldItems["Mod x"]], "a direct download names no mod, so nothing proves what replaces it");
        LoadoutItem.Load(Connection.Db, oldItems["Mod x"]).ParentId.Should().Be(MyMods(loadout));
        Items(group).Should().HaveCount(2).And.OnlyContain(item => item.Download.CollectionRevision.Id == second.Revision.Id);
    }

    [Fact]
    public async Task TheRefusedUpdateStillHandsOverWhatTheRepairDropped()
    {
        var s = await InstalledFirstRevision();
        await BreakTheWayTheEarlierUpdateDid(s);
        var third = await Publish(3, [.. s.SecondMods, await NexusMod("missing", 109, 1099, 40, inLibrary: false)], optional: [5, 7], rules: [Over("u3", "u1")]);

        var act = async () => await CollectionUpdater.Update(ServiceProvider, s.Group, third.Revision);

        var refused = (await act.Should().ThrowAsync<CollectionUpdateRefusedException>()).Which;
        refused.Message.Should().Contain("Mod missing");
        refused.Repaired.Dropped.Select(mod => mod.Group).Should().Equal([Single(s.Loadout, Dr).LoadoutItemGroupId], "the dialog still asks about dr");
        HoldsExactly(s, s.Second, [], userRules: 0);
    }

    [Fact]
    public async Task RepairLeavesAnotherCollectionAlone()
    {
        var s = await InstalledFirstRevision();
        var otherManifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Other", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = [await NexusMod("cf", 104, OldCf, 2, inLibrary: false), await NexusMod("own", 300, 3001, 33)],
        };
        var other = await Offline.Create(otherManifest, null, 1, slug: "other");
        var otherGroup = await Await(InstallCollectionJob.Create(ServiceProvider, s.Loadout, other.File, other.Revision, other.Items));
        var otherBefore = Items(otherGroup).Select(item => (item.Id, item.Download.Id, item.AsLoadoutItemGroup().AsLoadoutItem().ParentId.Value)).ToArray();
        await BreakTheWayTheEarlierUpdateDid(s);

        await CollectionUpdater.Repair(ServiceProvider, s.Group);

        Items(otherGroup).Select(item => (item.Id, item.Download.Id, item.AsLoadoutItemGroup().AsLoadoutItem().ParentId.Value)).Should().Equal(otherBefore);
        NexusCollectionLoadoutGroup.Load(Connection.Db, otherGroup.Id).Revision.Id.Should().Be(other.Revision.Id);
        HoldsExactly(s, s.Second, Items(otherGroup).Select(item => item.AsLoadoutItemGroup().LoadoutItemGroupId).ToArray());
    }

    [Fact]
    public async Task RepairIsReportedAsAnUpdateWhileItRuns()
    {
        var s = await InstalledFirstRevision();
        await BreakTheWayTheEarlierUpdateDid(s);
        var states = new List<bool>();
        using var subscription = CollectionUpdater.ObserveIsUpdating(s.Group.Collection, s.Loadout).Subscribe(states.Add);

        await CollectionUpdater.Repair(ServiceProvider, s.Group);

        states.Should().Equal([false, true, false], "the page keeps its install actions off for the repair");
    }

    [Fact]
    public async Task RepairChangesNothingInAHealthyCollection()
    {
        var s = await InstalledFirstRevision();
        var before = Items(s.Group).Select(item => (item.Id, item.Download.Id)).ToArray();

        var result = await CollectionUpdater.Repair(ServiceProvider, s.Group);

        result.ChangedAnything.Should().BeFalse();
        Items(s.Group).Select(item => (item.Id, item.Download.Id)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task TheNextUpdateRepairsACollectionAnEarlierUpdateBroke()
    {
        var s = await InstalledFirstRevision();
        await BreakTheWayTheEarlierUpdateDid(s);
        var third = await Publish(3, s.SecondMods, optional: [5, 7], rules: [Over("u3", "u1")]);
        using var installs = CountInstallJobs(third.Revision);

        var result = await CollectionUpdater.Update(ServiceProvider, s.Group, third.Revision);

        result.Error.Should().BeNull();
        HoldsExactly(s, third, []);
        result.Dropped.Select(mod => mod.Group).Should().Equal(Single(s.Loadout, Dr).LoadoutItemGroupId);
        installs.Count.Should().Be(1);
    }

    [Fact]
    public async Task AnOldRevisionIsSupersededOnceTheCollectionIsUpdatedPastIt()
    {
        var s = await InstalledFirstRevision();
        var otherLoadout = await CreateLoadout();
        var firstSuperseded = CollectionUpdater.ObserveIsSuperseded(Connection, s.First.Revision, s.Loadout);
        (await firstSuperseded.FirstAsync()).Should().BeFalse("revision 1 is the installed one");
        var becomesSuperseded = firstSuperseded.Where(static isSuperseded => isSuperseded).FirstAsync().ToTask();

        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        (await becomesSuperseded.WaitAsync(Timeout)).Should().BeTrue("the menu hides revision 1 as soon as the update switches the collection");
        (await CollectionUpdater.ObserveIsSuperseded(Connection, s.Second.Revision, s.Loadout).FirstAsync()).Should().BeFalse("revision 2 is installed");
        (await CollectionUpdater.ObserveIsSuperseded(Connection, s.First.Revision, otherLoadout).FirstAsync()).Should().BeFalse("another loadout doesn't have the collection");
    }
}
