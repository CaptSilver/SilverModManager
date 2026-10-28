using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Collections.Tests;

using Revision = (NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items);

/// <summary>
/// Updates an installed collection from one revision to another. Revision 1 holds mods a, b, c and d (rule: a over d); revision 2
/// keeps a and d, swaps b's file, drops c, adds e (rule: d over a). Nothing touches the network.
/// </summary>
public class CollectionUpdateTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionUpdateTests>(helper)
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

    /// <summary>A direct download whose archive is in the library. <paramref name="slot"/> is the mod's identity across revisions; the archive is its file.</summary>
    private async Task<Mod> MakeMod(string slot, int archive, bool plain = true, FomodOption[]? choices = null, bool inLibrary = true)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, md5) = await Offline.CreateFomodMod(folder.Path, archive, plain);
        if (inLibrary) await Offline.AddDirectDownload(path, md5);
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
            Choices = choices is null ? null : new Choices { Type = ChoicesType.fomod, Options = choices },
        };
    }

    private static ModRule Over(string winner, string loser) => new()
    {
        Type = VortexModRuleType.After,
        Source = new VortexModReference { Tag = winner },
        Other = new VortexModReference { Tag = loser },
    };

    private async Task<Revision> Publish(int revisionNumber, Mod[] mods, ModRule[] rules, int[]? optional = null)
    {
        var manifest = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods,
            ModRules = rules,
        };
        var (file, revision, _) = await Offline.Create(manifest, optional, revisionNumber);
        return (file, revision, CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required));
    }

    private sealed record Scenario(Revision First, Revision Second, LoadoutId Loadout, NexusCollectionLoadoutGroup.ReadOnly Group);

    private async Task<Scenario> InstalledFirstRevision(bool withRules = true, ModRule[]? secondRules = null)
    {
        var a = await MakeMod("a", 0);
        var c = await MakeMod("c", 2);
        var d = await MakeMod("d", 3);
        var first = await Publish(1, [a, await MakeMod("b", 1), c, d], withRules ? [Over("a", "d")] : []);
        var second = await Publish(2, [a, await MakeMod("b", 11), d, await MakeMod("e", 4)], secondRules ?? (withRules ? [Over("d", "a")] : []));
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        return new Scenario(first, second, loadout, group);
    }

    private static async Task<NexusCollectionLoadoutGroup.ReadOnly> Await(IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> job)
    {
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    private LoadoutItemGroup.ReadOnly Mod(LoadoutId loadout, string name) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup().Single(item => item.AsLoadoutItem().Name == name);

    private bool Exists(LoadoutId loadout, string name) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup().Any(item => item.AsLoadoutItem().Name == name);

    private string[] CollectionMods(NexusCollectionLoadoutGroup.ReadOnly group) =>
        NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).GetCollectionItems().Select(item => item.AsLoadoutItemGroup().AsLoadoutItem().Name).Order().ToArray();

    private CollectionGroupId UserCollection(LoadoutId loadout) =>
        CollectionGroupId.From(Loadout.Load(Connection.Db, loadout).MutableCollections().First().CollectionId);

    private string[] OrderOf(LoadoutId loadout, params string[] names) =>
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout).Where(row => row.Target.IsValid()).OrderByDescending(row => row.Priority)
            .Select(row => LoadoutItem.Load(Connection.Db, row.TargetId).Name).Where(names.Contains).ToArray();

    [Fact]
    public async Task SwitchKeepsTheGroupAndPointsUnchangedModsAtTheNewRevision()
    {
        var s = await InstalledFirstRevision();
        var aBefore = Mod(s.Loadout, "a-0.zip");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(aBefore.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        var orderBefore = OrderOf(s.Loadout, "a-0.zip", "d-3.zip");

        var switched = await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        switched.Group.Id.Should().Be(s.Group.Id, "the collection keeps its entry");
        NexusCollectionLoadoutGroup.All(Connection.Db).Should().ContainSingle();
        NexusCollectionLoadoutGroup.Load(Connection.Db, s.Group.Id).Revision.Id.Should().Be(s.Second.Revision.Id);

        var a = Mod(s.Loadout, "a-0.zip");
        a.Id.Should().Be(aBefore.Id, "an unchanged mod is the same mod, not a reinstall");
        a.AsLoadoutItem().IsDisabled.Should().BeTrue();
        OrderOf(s.Loadout, "a-0.zip", "d-3.zip").Should().Equal(orderBefore, "kept mods stay where they were");
        NexusCollectionItemLoadoutGroup.Load(Connection.Db, a.Id).Download.CollectionRevision.Id.Should().Be(s.Second.Revision.Id);

        CollectionMods(s.Group).Should().Equal("a-0.zip", "d-3.zip");
        var b = Mod(s.Loadout, "b-1.zip");
        b.AsLoadoutItem().ParentId.Should().Be(LoadoutItemGroupId.From(UserCollection(s.Loadout).Value.Value), "the old version stays until its replacement is installed");
        NexusCollectionItemLoadoutGroup.Load(Connection.Db, b.Id).IsValid().Should().BeFalse();
        switched.Items.Select(item => item.Name).Should().BeEquivalentTo(["Mod b", "Mod e"]);
    }

    [Fact]
    public async Task SwitchLeavesNothingPointingAtTheOldRevision()
    {
        var s = await InstalledFirstRevision();

        await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        var items = Connection.Db.Datoms(NexusCollectionItemLoadoutGroup.Download).Select(datom => NexusCollectionItemLoadoutGroup.Load(Connection.Db, datom.E)).ToArray();
        items.Should().OnlyContain(item => item.Download.CollectionRevision.Id == s.Second.Revision.Id);

        // Deleting the old revision must not take any installed mod with it.
        await ServiceProvider.GetRequiredService<CollectionDownloader>().DeleteRevision(CollectionRevisionMetadataId.From(s.First.Revision.Id));
        foreach (var item in items) NexusCollectionItemLoadoutGroup.Load(Connection.Db, item.Id).Download.IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task SwitchMovesDroppedModsToTheLoadoutsOwnCollectionAsPlainMods()
    {
        var s = await InstalledFirstRevision();

        var switched = await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        switched.Dropped.Select(dropped => dropped.Name).Should().Equal("c-2.zip");
        var c = Mod(s.Loadout, "c-2.zip");
        c.AsLoadoutItem().ParentId.Should().Be(LoadoutItemGroupId.From(UserCollection(s.Loadout).Value.Value));
        NexusCollectionItemLoadoutGroup.Load(Connection.Db, c.Id).IsValid().Should().BeFalse("it no longer belongs to the collection");
        c.Children.OfTypeLoadoutItemWithTargetPath().Should().NotBeEmpty("its files stay");
    }

    [Fact]
    public async Task RemovingDroppedModsDeletesThem()
    {
        var s = await InstalledFirstRevision();
        var switched = await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        await CollectionUpdater.RemoveDropped(ServiceProvider, switched.Dropped);

        Exists(s.Loadout, "c-2.zip").Should().BeFalse();
        Exists(s.Loadout, "a-0.zip").Should().BeTrue();
    }

    [Fact]
    public async Task SwitchReplacesTheOldRevisionsRulesAndKeepsTheUsersOwn()
    {
        var s = await InstalledFirstRevision();
        LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Should().ContainSingle().Which.Kind.Should().Be(GroupRuleKind.After);
        var a = Mod(s.Loadout, "a-0.zip");
        var c = Mod(s.Loadout, "c-2.zip");
        var d = Mod(s.Loadout, "d-3.zip");
        (await LoadoutManager.AddGroupRule(s.Loadout, a.LoadoutItemGroupId, c.LoadoutItemGroupId, GroupRuleKind.After)).IsCycle.Should().BeFalse();

        await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);
        var left = LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Should().ContainSingle("only the user's own rule is left").Subject;
        (left.SourceId, left.OtherId).Should().Be((a.LoadoutItemGroupId, c.LoadoutItemGroupId));

        await LoadoutManager.ApplyCollectionDownloadRules(NexusCollectionLoadoutGroupId.From(s.Group.Id));
        LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Select(rule => (rule.SourceId, rule.OtherId)).Should()
            .BeEquivalentTo([(a.LoadoutItemGroupId, c.LoadoutItemGroupId), (d.LoadoutItemGroupId, a.LoadoutItemGroupId)], "revision 2 puts d over a");
    }

    [Fact]
    public async Task SwitchRefusesAndChangesNothingWhenAnArchiveIsMissing()
    {
        var a = await MakeMod("a", 0);
        var first = await Publish(1, [a, await MakeMod("b", 1)], []);
        var second = await Publish(2, [a, await MakeMod("b", 11, inLibrary: false)], []);
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));

        var act = async () => await CollectionUpdater.Switch(ServiceProvider, group, second.Revision);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Mod b");
        NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).Revision.Id.Should().Be(first.Revision.Id);
        CollectionMods(group).Should().Equal("a-0.zip", "b-1.zip");
    }

    [Fact]
    public async Task UpdateInstallsWhatChangedInTheSameCollectionEntry()
    {
        var s = await InstalledFirstRevision();

        var result = await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        NexusCollectionLoadoutGroup.All(Connection.Db).Should().ContainSingle("updating never adds a second collection");
        result.Group.Id.Should().Be(s.Group.Id);
        result.Group.Revision.Id.Should().Be(s.Second.Revision.Id);
        CollectionMods(result.Group).Should().Equal("a-0.zip", "b-11.zip", "d-3.zip", "e-4.zip");
        Exists(s.Loadout, "b-1.zip").Should().BeFalse("the old version goes once its replacement is installed");
        result.Failed.Should().BeEmpty();
        result.Dropped.Select(dropped => dropped.Name).Should().Equal("c-2.zip");

        var rule = LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Should().ContainSingle().Subject;
        LoadoutItem.Load(Connection.Db, rule.SourceId).Name.Should().Be("d-3.zip");
        LoadoutItem.Load(Connection.Db, rule.OtherId).Name.Should().Be("a-0.zip");
    }

    [Fact]
    public async Task UpdatePutsAReplacedModWhereTheOldOneStoodAndKeepsItSwitchedOff()
    {
        var s = await InstalledFirstRevision(withRules: false);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(Mod(s.Loadout, "b-1.zip").Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        // Put b at the very top so its slot is not where the install would drop a new mod anyway.
        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, s.Loadout).ToDictionary(row => row.TargetId, row => row.LoadoutItemGroupPriorityId);
        var bGroup = Mod(s.Loadout, "b-1.zip").LoadoutItemGroupId;
        var lowest = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, s.Loadout).OrderBy(row => row.Priority).First();
        (await LoadoutManager.ResolveFileConflicts([rows[bGroup]], lowest.LoadoutItemGroupPriorityId)).IsRuleViolation.Should().BeFalse();
        var before = OrderOf(s.Loadout, "a-0.zip", "b-1.zip", "d-3.zip");

        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        OrderOf(s.Loadout, "a-0.zip", "b-11.zip", "d-3.zip").Select(name => name == "b-11.zip" ? "b-1.zip" : name)
            .Should().Equal(before, "the new file takes the old file's place");
        Mod(s.Loadout, "b-11.zip").AsLoadoutItem().IsDisabled.Should().BeTrue("the user had switched the old one off");
        Exists(s.Loadout, "b-1.zip").Should().BeFalse();
    }

    private static FomodOption[] NoSuchOption() =>
    [
        new FomodOption { name = "Step 1", groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "No such option" }] }] },
    ];

    [Fact]
    public async Task AReplacementThatFailsLeavesTheOldVersionAsTheUsersOwnModAndTheCollectionEnabled()
    {
        var a = await MakeMod("a", 0);
        var first = await Publish(1, [a, await MakeMod("b", 1)], []);
        _window.SupportsUserInteraction = false;
        var second = await Publish(2, [a, await MakeMod("b", 11, plain: false, choices: NoSuchOption())], []);
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeFalse();

        var result = await CollectionUpdater.Update(ServiceProvider, group, second.Revision);

        result.Group.Revision.Id.Should().Be(second.Revision.Id);
        CollectionMods(result.Group).Should().Equal("a-0.zip");
        var old = Mod(loadout, "b-1.zip");
        old.AsLoadoutItem().ParentId.Should().Be(LoadoutItemGroupId.From(UserCollection(loadout).Value.Value));
        old.Children.OfTypeLoadoutItemWithTargetPath().Should().NotBeEmpty("nothing of the old version was lost");
        result.Failed.Select(failed => failed.Name).Should().Equal("Mod b");
        result.OldVersionsKept.Should().Be(1);
        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeFalse("a failed update must not switch off a collection that was on");
        var brokenDownload = second.Items.Single(item => item.Name == "Mod b");
        CollectionDownloader.GetStatus(brokenDownload, result.Group.AsCollectionGroup(), Connection.Db).IsInstalled(out _).Should().BeFalse("the page offers to install it again");
    }

    [Fact]
    public async Task ACancelledUpdateKeepsEveryOldVersion()
    {
        var a = await MakeMod("a", 0);
        var first = await Publish(1, [a, await MakeMod("b", 1)], []);
        var release = new TaskCompletionSource();
        var windowOpen = new TaskCompletionSource();
        _window.BeforeAnswer = async _ =>
        {
            windowOpen.TrySetResult();
            await release.Task;
        };
        var second = await Publish(2, [a, await MakeMod("b", 11, plain: false, choices: NoSuchOption())], []);
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        using var cancel = new CancellationTokenSource();

        var update = CollectionUpdater.Update(ServiceProvider, group, second.Revision, cancel.Token).AsTask();
        await windowOpen.Task.WaitAsync(Timeout);
        await cancel.CancelAsync();
        _window.Cancel = true; // the held window is closed, so the replacement never installs
        release.SetResult();
        var act = async () => await update.WaitAsync(Timeout);

        await act.Should().ThrowAsync<Exception>();
        Mod(loadout, "b-1.zip").Children.OfTypeLoadoutItemWithTargetPath().Should().NotBeEmpty("its replacement never installed, so the old version is still there");
        Mod(loadout, "a-0.zip").Should().NotBeNull();
    }

    [Fact]
    public async Task ARulesCycleAfterTheInstallStillFinishesTheReplacements()
    {
        var s = await InstalledFirstRevision(withRules: false, secondRules: [Over("d", "a")]);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(Mod(s.Loadout, "b-1.zip").Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        // The user's rule and revision 2's rule contradict each other; revision 2's rules are applied after every mod is installed.
        (await LoadoutManager.AddGroupRule(s.Loadout, Mod(s.Loadout, "a-0.zip").LoadoutItemGroupId, Mod(s.Loadout, "d-3.zip").LoadoutItemGroupId, GroupRuleKind.After)).IsCycle.Should().BeFalse();

        var result = await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        result.Error.Should().BeOfType<CollectionRulesCycleException>("the error is reported, not thrown away");
        Exists(s.Loadout, "b-1.zip").Should().BeFalse("its replacement is installed, so having both would run two versions");
        var replacement = Mod(s.Loadout, "b-11.zip");
        replacement.AsLoadoutItem().IsDisabled.Should().BeTrue("the user had switched the old one off");
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, s.Loadout).Should().Contain(row => row.TargetId == replacement.LoadoutItemGroupId);
        result.Dropped.Select(dropped => dropped.Name).Should().Equal("c-2.zip");
    }

    [Fact]
    public async Task ARuleThatCannotBeCarriedOverIsReported()
    {
        // Revision 2 puts a over b; the user's rule on the old b puts b over a, which the new b can't take over.
        var s = await InstalledFirstRevision(withRules: false, secondRules: [Over("a", "b")]);
        var a = Mod(s.Loadout, "a-0.zip").LoadoutItemGroupId;
        (await LoadoutManager.AddGroupRule(s.Loadout, Mod(s.Loadout, "b-1.zip").LoadoutItemGroupId, a, GroupRuleKind.After)).IsCycle.Should().BeFalse();

        var result = await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        result.Error.Should().BeNull();
        result.SkippedRules.Should().ContainSingle().Which.Should().Contain("b-11.zip").And.Contain("a-0.zip");
        Exists(s.Loadout, "b-1.zip").Should().BeFalse();
    }

    [Fact]
    public async Task SwitchRefusesARevisionThatIsNotNewer()
    {
        var s = await InstalledFirstRevision();
        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        var act = async () => await CollectionUpdater.Switch(ServiceProvider, s.Group, s.First.Revision);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("older");
        NexusCollectionLoadoutGroup.Load(Connection.Db, s.Group.Id).Revision.Id.Should().Be(s.Second.Revision.Id);
    }

    [Fact]
    public async Task OnlyANewerRevisionIsAnUpdateOfTheInstalledGroup()
    {
        var s = await InstalledFirstRevision();

        CollectionUpdater.IsUpdateTo(s.Group, s.Second.Revision).Should().BeTrue();
        CollectionUpdater.IsUpdateTo(s.Group, s.First.Revision).Should().BeFalse("it is the installed revision");
        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);
        CollectionUpdater.IsUpdateTo(NexusCollectionLoadoutGroup.Load(Connection.Db, s.Group.Id), s.First.Revision).Should().BeFalse("revision 1 is older than the installed 2");
    }

    [Fact]
    public async Task AUsersRuleOnAChangedModMovesToItsReplacement()
    {
        var s = await InstalledFirstRevision(withRules: false);
        var a = Mod(s.Loadout, "a-0.zip").LoadoutItemGroupId;
        (await LoadoutManager.AddGroupRule(s.Loadout, Mod(s.Loadout, "b-1.zip").LoadoutItemGroupId, a, GroupRuleKind.After)).IsCycle.Should().BeFalse();

        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        var rule = LoadoutGroupRule.FindByLoadout(Connection.Db, s.Loadout).Should().ContainSingle().Subject;
        (rule.SourceId, rule.OtherId, rule.Kind).Should().Be((Mod(s.Loadout, "b-11.zip").LoadoutItemGroupId, a, GroupRuleKind.After));
    }

    [Fact]
    public async Task WithoutTheOldCollectionFileModsWithTheSameFileAreKept()
    {
        var s = await InstalledFirstRevision();
        var aBefore = Mod(s.Loadout, "a-0.zip").Id;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(s.First.File.Id, recursive: false);
            await tx.Commit();
        }

        var switched = await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        switched.Plan.Kept.Select(kept => kept.Installed.Name).Should().BeEquivalentTo(["a-0.zip", "d-3.zip"], "the file is the same and its settings can't be compared");
        Mod(s.Loadout, "a-0.zip").Id.Should().Be(aBefore);
    }

    [Fact]
    public async Task DetachedModsGoToANewDefaultCollectionWhenMyModsIsGone()
    {
        var s = await InstalledFirstRevision();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(UserCollection(s.Loadout).Value, recursive: true);
            await tx.Commit();
        }

        await CollectionUpdater.Switch(ServiceProvider, s.Group, s.Second.Revision);

        var parent = LoadoutItem.Load(Connection.Db, Mod(s.Loadout, "c-2.zip").AsLoadoutItem().ParentId.Value);
        parent.Name.Should().Be("My Mods");
        CollectionGroup.Load(Connection.Db, parent.Id).IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task ASingleModInstallIntoTheGroupBlocksAnUpdate()
    {
        var s = await InstalledFirstRevision();
        var job = await InstallCollectionDownloadJob.Create(ServiceProvider, s.Loadout, s.First.Items[0], CancellationToken.None);

        CollectionUpdater.Blocks(job, s.Group).Should().BeTrue();
    }

    [Fact]
    public async Task UpdatingIsReportedWhileAnUpdateRuns()
    {
        var s = await InstalledFirstRevision();
        var seen = new List<bool>();
        using var subscription = CollectionUpdater.ObserveIsUpdating(s.Second.Revision.Collection, s.Loadout).Subscribe(seen.Add);

        await CollectionUpdater.Update(ServiceProvider, s.Group, s.Second.Revision);

        seen.Should().Equal(false, true, false);
    }

    [Fact]
    public async Task ReorderedFileListsAreTheSameInstallSettings()
    {
        var options = ServiceProvider.GetRequiredService<System.Text.Json.JsonSerializerOptions>();
        var one = new PathAndHash { Path = "a.txt", MD5 = Md5Value.From((UInt128)1) };
        var two = new PathAndHash { Path = "b.txt", MD5 = Md5Value.From((UInt128)2) };
        var template = await MakeMod("x", 0);

        string Spec(params PathAndHash[] hashes) => CollectionUpdater.SpecOf(new Mod { Name = "x", DomainName = template.DomainName, Source = template.Source, Hashes = hashes }, options);

        Spec(one, two).Should().Be(Spec(two, one));
        Spec(one).Should().NotBe(Spec(two));
    }

    private static ModSource Bundled(string expression) => new() { Type = ModSourceType.Bundle, FileExpression = expression, LogicalFilename = expression };

    private static Mod BundledMod(string slot, string expression) => new()
    {
        Name = $"Mod {slot}",
        DomainName = GameDomain.From("cyberpunk2077"),
        Source = Bundled(expression),
    };

    [Fact]
    public async Task BundledModsAreKeptWhenTheirFilesAreTheSameAndReplacedWhenTheyChanged()
    {
        Mod[] mods = [BundledMod("stay", "stay"), BundledMod("move", "move")];
        Task<Revision> PublishBundled(int number, string moveContent)
        {
            var manifest = new CollectionRoot { Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") }, Mods = mods };
            var files = new Dictionary<string, (string Path, string Content)[]>
            {
                ["stay"] = [("stay.txt", "the same")],
                ["move"] = [("move.txt", moveContent)],
            };
            return Offline.Create(manifest, null, number, files).ContinueWith(task => (task.Result.File, task.Result.Revision, CollectionDownloader.GetItems(task.Result.Revision, CollectionDownloader.ItemType.Required)));
        }

        var first = await PublishBundled(1, "old content");
        var second = await PublishBundled(2, "new content");
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, first.Items));
        CollectionMods(group).Should().HaveCount(2);
        var stayGroup = NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).GetCollectionItems().Single(item => item.AsLoadoutItemGroup().AsLoadoutItem().Name == "Mod stay").Id;
        var moveGroup = NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).GetCollectionItems().Single(item => item.AsLoadoutItemGroup().AsLoadoutItem().Name == "Mod move").Id;

        var result = await CollectionUpdater.Update(ServiceProvider, group, second.Revision);

        var items = result.Group.GetCollectionItems().ToArray();
        items.Should().HaveCount(2);
        items.Select(item => item.Id).Should().Contain(stayGroup, "identical bundled files are the same mod").And.NotContain(moveGroup, "its files changed, so it was installed again");
        var stay = NexusCollectionBundledLoadoutGroup.Load(Connection.Db, stayGroup);
        stay.BundleDownload.AsCollectionDownload().CollectionRevision.Id.Should().Be(second.Revision.Id);
        stay.CollectionLibraryFile.Id.Should().Be(second.File.Id);
        Connection.Db.Datoms(NexusCollectionBundledLoadoutGroup.BundleDownload).Select(datom => NexusCollectionBundledLoadoutGroup.Load(Connection.Db, datom.E))
            .Should().OnlyContain(bundled => bundled.BundleDownload.AsCollectionDownload().CollectionRevision.Id == second.Revision.Id);
        Mod(loadout, "Mod move").Children.OfTypeLoadoutItemWithTargetPath().Single().AsLoadoutItem().Name.Should().EndWith("move.txt");
        BundledArchives().Should().BeEmpty("the replaced bundled mod's detached archive goes with it");
    }

    private ManuallyCreatedArchive.ReadOnly[] BundledArchives() => ManuallyCreatedArchive.All(Connection.Db)
        .Where(archive => archive.Source == ManuallyCreatedArchive.CreationSource.CollectionBundled).ToArray();

    [Fact]
    public async Task RemovingADroppedBundledModAlsoRemovesItsDetachedArchive()
    {
        var manifest1 = new CollectionRoot { Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") }, Mods = [BundledMod("gone", "gone")] };
        var manifest2 = new CollectionRoot { Info = manifest1.Info, Mods = [] };
        var first = await Offline.Create(manifest1, null, 1, new Dictionary<string, (string Path, string Content)[]> { ["gone"] = [("gone.txt", "content")] });
        var second = await Offline.Create(manifest2, null, 2);
        var loadout = await CreateLoadout();
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, CollectionDownloader.GetItems(first.Revision, CollectionDownloader.ItemType.Required)));

        var switched = await CollectionUpdater.Switch(ServiceProvider, group, second.Revision);
        BundledArchives().Should().ContainSingle("a kept bundled mod needs an archive of its own");

        await CollectionUpdater.RemoveDropped(ServiceProvider, switched.Dropped);

        BundledArchives().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateKeepsTheUsersPickOfOptionalMods()
    {
        var x = await MakeMod("x", 0);
        var y = await MakeMod("y", 1);
        var z = await MakeMod("z", 2);
        var w = await MakeMod("w", 3);
        // x required, y optional (installed), z optional (not installed), w optional (not installed).
        var first = await Publish(1, [x, y, z, w], [], optional: [1, 2, 3]);
        var loadout = await CreateLoadout();
        var items = new[] { first.Items.Single(), CollectionDownloader.GetItems(first.Revision, CollectionDownloader.ItemType.Optional).Single(item => item.Name == "Mod y") };
        var group = await Await(InstallCollectionJob.Create(ServiceProvider, loadout, first.File, first.Revision, items));
        CollectionMods(group).Should().Equal("x-0.zip", "y-1.zip");

        // Revision 2: x is optional now, w is required now.
        var second = await Publish(2, [x, y, z, w], [], optional: [0, 1, 2]);

        await CollectionUpdater.Update(ServiceProvider, group, second.Revision);

        CollectionMods(group).Should().Equal("w-3.zip", "x-0.zip", "y-1.zip");
        NexusCollectionItemLoadoutGroup.Load(Connection.Db, Mod(loadout, "x-0.zip").Id).IsRequired.Should().BeFalse();
        NexusCollectionItemLoadoutGroup.Load(Connection.Db, Mod(loadout, "w-3.zip").Id).IsRequired.Should().BeTrue();
    }

    [Fact]
    public async Task InstalledGroupIsFoundForAnyRevisionOfTheCollectionInThatLoadoutOnly()
    {
        var s = await InstalledFirstRevision();
        var otherLoadout = await CreateLoadout();

        CollectionUpdater.GetInstalledGroup(s.Second.Revision.Collection, s.Loadout, Connection.Db).Value.Id.Should().Be(s.Group.Id);
        CollectionUpdater.GetInstalledGroup(s.Second.Revision.Collection, otherLoadout, Connection.Db).HasValue.Should().BeFalse();
    }
}
