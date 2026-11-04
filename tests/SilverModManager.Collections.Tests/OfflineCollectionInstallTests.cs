using System.IO.Compression;
using System.Text.Json;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Collections.Tests;

/// <summary>
/// Installs a collection built from local archives. Nothing here touches the network: the collection
/// entities are written to the database directly and the mods are zips created on disk.
/// </summary>
public class OfflineCollectionInstallTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<OfflineCollectionInstallTests>(helper)
{
    // Stands in for the guided installer window. Assigned before the base constructor builds the services.
    private readonly ScriptedGuidedInstallerState _window = new();

    // Counts how often an install reaches the manual installer; a collection install must never get there.
    private readonly CountingAdvancedInstallerHandler _manualInstaller = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddSingleton(_window)
            .AddSingleton<IAdvancedInstallerHandler>(_manualInstaller)
            .AddTransient<IGuidedInstaller>(_ => _window.Create());
    }

    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static FomodOption[] Choices(string firstStepOption = "Option A", string secondStepOption = "Option C") =>
    [
        new FomodOption
        {
            name = "Step 1",
            groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = firstStepOption }] }],
        },
        new FomodOption
        {
            name = "Step 2",
            groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = secondStepOption }] }],
        },
    ];

    private Task<(NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items)> CreateCollection(
        (FomodOption[]? Choices, string Name)[] fomodMods, int[]? optionalIndexes = null,
        (int Source, int Other, CollectionDownloadRuleType Type)[]? rules = null,
        Func<IReadOnlyList<Mod>, SilverLoadOrderEntry[]>? silverLoadOrder = null,
        ModShape[]? shapes = null) =>
        new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager)
            .Create(fomodMods, optionalIndexes, rules, silverLoadOrder, shapes);

    /// <summary>Runs the download job the way Review does: attended, on its own.</summary>
    private async Task RunDownload(InstallCollectionDownloadJob job)
    {
        async Task Run() => await ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
        await Run().WaitAsync(Timeout);
    }

    private Optional<NexusCollectionPendingInstall.ReadOnly> PendingRow(NexusCollectionLoadoutGroup.ReadOnly group, CollectionDownload.ReadOnly download) =>
        PendingInstalls.Find(Connection.Db, group.Id, CollectionDownloadId.From(download.Id));

    private static async Task<NexusCollectionLoadoutGroup.ReadOnly> AwaitJob(IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> job)
    {
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    [Fact]
    public async Task InstallingACollectionWithOurKeyAppliesItsOrder()
    {
        var mods = Enumerable.Range(1, 3).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods, silverLoadOrder: m =>
        [
            new SilverLoadOrderEntry { Md5 = m[2].Source.Md5, Name = m[2].Name },
            new SilverLoadOrderEntry { Md5 = Md5Value.From((UInt128)12345), Name = "Never downloaded" },
            new SilverLoadOrderEntry { Md5 = m[0].Source.Md5, Name = m[0].Name },
            new SilverLoadOrderEntry { Md5 = m[1].Source.Md5, Name = m[1].Name },
        ]);
        var loadout = await CreateLoadout();

        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        var db = Connection.Db;
        var priorities = LoadoutItemGroupPriority.FindByLoadout(db, loadout)
            .ToDictionary(row => row.TargetId, row => row.Priority.Value);
        var byPriority = LoadoutItemGroup.Load(db, group.Id).Children
            .OfTypeLoadoutItemGroup()
            .OrderByDescending(child => priorities[child.LoadoutItemGroupId])
            .Select(child => child.AsLoadoutItem().Name)
            .ToArray();
        // Installed groups are named after their archives (mod0.zip is "Plain mod 1").
        byPriority.Should().Equal("mod2.zip", "mod0.zip", "mod1.zip");

        var rows = ModOrderDroppedEntry.FindByLoadout(db, loadout).ToArray();
        rows.Should().ContainSingle();
        rows[0].Item.Should().Be("Never downloaded");
        rows[0].Source.Should().Be("collection Offline");
        rows[0].ContainerId.Should().Be(group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId);
    }

    [Fact]
    public async Task ARuleThatContradictsTheCollectionsOrderWinsAndIsReported()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        // The rule puts mod 0 over mod 1; the order key lists mod 1 first.
        var (file, revision, items) = await CreateCollection(mods,
            rules: [(0, 1, CollectionDownloadRuleType.After)],
            silverLoadOrder: m => [Order(m[1]), Order(m[0]), Order(m[2])]);
        var loadout = await CreateLoadout();

        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        var nameOf = (LoadoutItemGroupId id) => LoadoutItem.Load(group.Db, id).Name;
        var order = LoadoutItemGroupPriority.FindByLoadout(group.Db, loadout).OrderBy(p => p.Priority).Select(p => nameOf(p.TargetId)).ToList();
        order.Should().Equal("Fomod mod 2", "Fomod mod 1", "Fomod mod 0");

        var rows = ModOrderDroppedEntry.FindByLoadout(group.Db, loadout).ToArray();
        rows.Should().ContainSingle();
        rows[0].Item.Should().Be("the listed order of Fomod mod 1 and Fomod mod 0");
        rows[0].Reason.Should().Be("the stored rule 'Fomod mod 0 wins over Fomod mod 1' puts them the other way round");
        rows[0].Source.Should().Be("collection Offline");
    }

    [Fact]
    public async Task InstallingACollectionSeedsTheRedModOrderFromItsOrderWithoutAnyoneUpdatingTheSortOrders()
    {
        var mods = Enumerable.Range(0, 3).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods, silverLoadOrder: m => [Order(m[2]), Order(m[0]), Order(m[1])]);
        var loadout = await CreateLoadout();

        await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        var variety = GameInstallation.GetGame().SortOrderManager.GetSortOrderVarieties().OfType<RedModSortOrderVariety>().Single();
        var sortOrder = variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value;
        // The first folder in the REDmod list wins, so the collection's first mod takes index 0.
        variety.GetSortOrderItems(sortOrder, Connection.Db).OrderBy(item => item.SortIndex).Select(item => item.Key.Key)
            .Should().Equal("plain2", "plain0", "plain1");
    }

    private static SilverLoadOrderEntry Order(Mod mod) => new() { Md5 = mod.Source.Md5, Name = mod.Name };

    private static SilverLoadOrderEntry Order(Mod mod, bool enabled) => new() { Md5 = mod.Source.Md5, Name = mod.Name, Enabled = enabled };

    private string[] ChildrenWinnerFirst(NexusCollectionLoadoutGroup.ReadOnly group) => ContainerOrder
        .ChildrenWinnerFirst(Connection.Db, group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId)
        .Select(child => child.AsLoadoutItem().Name)
        .ToArray();

    private LoadoutItemGroup.ReadOnly Child(NexusCollectionLoadoutGroup.ReadOnly group, string name) =>
        LoadoutItemGroup.Load(Connection.Db, group.Id).Children.OfTypeLoadoutItemGroup().Single(child => child.AsLoadoutItem().Name == name);

    /// <summary>
    /// A collection of four plain mods whose order key lists mod0, mod2 (optional, switched off), mod1, mod3, installed
    /// without its optional; then the user puts mod3 first and switches mod1 off.
    /// </summary>
    private async Task<(NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, NexusCollectionLoadoutGroup.ReadOnly Group, LoadoutId Loadout)> InstallAndRearrange()
    {
        var mods = Enumerable.Range(0, 4).Select(i => ((FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods, optionalIndexes: [2],
            silverLoadOrder: m => [Order(m[0], true), Order(m[2], false), Order(m[1], true), Order(m[3], true)]);
        var loadout = await CreateLoadout();
        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));
        ChildrenWinnerFirst(group).Should().Equal("mod0.zip", "mod1.zip", "mod3.zip");

        var rows = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout).ToDictionary(r => r.TargetId, r => r.LoadoutItemGroupPriorityId);
        (await LoadoutManager.ResolveFileConflicts([rows[Child(group, "mod3.zip").LoadoutItemGroupId]], rows[Child(group, "mod0.zip").LoadoutItemGroupId])).IsRuleViolation.Should().BeFalse();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(Child(group, "mod1.zip").Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        ChildrenWinnerFirst(group).Should().Equal("mod3.zip", "mod0.zip", "mod1.zip");
        return (file, revision, group, loadout);
    }

    private void TheUsersOrderAndSwitchHoldAndTheOptionalSitsInItsListedSlot(NexusCollectionLoadoutGroup.ReadOnly group)
    {
        // mod2 is listed right after mod0, so it lands below it, inside the user's order.
        ChildrenWinnerFirst(group).Should().Equal("mod3.zip", "mod0.zip", "mod2.zip", "mod1.zip");
        Child(group, "mod1.zip").AsLoadoutItem().IsDisabled.Should().BeTrue("the user switched it off and the key's switch is for new mods only");
        Child(group, "mod2.zip").AsLoadoutItem().IsDisabled.Should().BeTrue("the key lists the new mod switched off");
    }

    [Fact]
    public async Task InstallingTheOptionalModsKeepsTheUsersOrderAndSwitchesAndSlotsTheNewMod()
    {
        var (file, revision, group, loadout) = await InstallAndRearrange();

        await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional)));

        TheUsersOrderAndSwitchHoldAndTheOptionalSitsInItsListedSlot(group);
    }

    [Fact]
    public async Task InstallingOneOptionalFromTheCollectionPageLeavesTheUsersOwnModAboveTheCollection()
    {
        var (_, revision, group, loadout) = await InstallAndRearrange();
        LoadoutItemGroupId own;
        using (var tx = Connection.BeginTransaction())
        {
            var added = AddEmptyGroup(tx, loadout, "Own mod");
            var top = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout).Max(row => row.Priority.Value);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout, TargetId = added, Priority = ConflictPriority.From(top + 1) };
            var result = await tx.Commit();
            own = LoadoutItemGroupId.From(result[added].Value);
        }
        var optional = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional).Single();

        var job = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, optional, CancellationToken.None);
        async Task Run() => await ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
        await Run().WaitAsync(Timeout);

        var lowestFirst = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout).OrderBy(row => row.Priority).Select(row => row.TargetId).ToList();
        lowestFirst[^1].Should().Be(own, "a mod the user installed after the collection stays above it");
        TheUsersOrderAndSwitchHoldAndTheOptionalSitsInItsListedSlot(group);
    }

    [Fact]
    public async Task InstallingOneOptionalFromTheCollectionPagePutsItInItsListedSlot()
    {
        var (_, revision, group, loadout) = await InstallAndRearrange();
        var optional = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional).Single();

        var job = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, optional, CancellationToken.None);
        async Task Run() => await ServiceProvider.GetRequiredService<IJobMonitor>().Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
        await Run().WaitAsync(Timeout);

        TheUsersOrderAndSwitchHoldAndTheOptionalSitsInItsListedSlot(group);
    }

    [Fact]
    public async Task FomodModsWithSavedChoices_AllInstall()
    {
        var mods = Enumerable.Range(0, 6).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        var files = LoadoutItem.FindByLoadout(group.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
            .Order()
            .ToArray();

        files.Should().Equal(Enumerable.Range(0, 6).SelectMany(i => new[] { $"mod{i}_a.out.esp", $"mod{i}_c.out.esp" }).Order().ToArray());
    }

    [Fact]
    public async Task ModWithSavedChoicesThatDoNotApply_FailsWithAReasonAndTheRestInstall()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            (Choices("No such option"), "Broken mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        _window.SupportsUserInteraction = false; // headless: no window to fall back to

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        var pending = job.JobDefinition.PendingItems.Should().ContainSingle().Which;
        pending.Name.Should().Be("Broken mod");
        pending.Kind.Should().Be(ReviewKind.NeedsChoices);
        pending.Reason.Should().Contain("No such option");

        var installedNames = LoadoutItem.FindByLoadout(group.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
            .ToArray();
        installedNames.Should().Contain(["mod0_a.out.esp", "mod2_a.out.esp"]).And.NotContain("mod1_a.out.esp");
    }

    [Fact]
    public async Task Progress_CountsRiseAndEveryModIsReportedAsInstallingBeforeItCounts()
    {
        var mods = Enumerable.Range(0, 6).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var snapshots = new List<CollectionInstallProgress.Snapshot>();
        using var subscription = job.JobDefinition.Progress.Changes.Subscribe(snapshots.Add);
        await AwaitJob(job);

        // The job starts on the thread pool, so subscribing can still replay the empty snapshot from before it began.
        var seen = snapshots.SkipWhile(s => ReferenceEquals(s, CollectionInstallProgress.Snapshot.Empty)).ToArray();
        seen.Select(s => s.Installed).Should().BeInAscendingOrder();
        seen.Should().OnlyContain(s => s.Total == 6 && s.Installed + s.Failed.Count + s.Pending.Count + s.Installing.Count <= s.Total);
        seen.SelectMany(s => s.Installing).Distinct().Should().BeEquivalentTo(mods.Select(m => m.Item2));

        var last = seen[^1];
        last.IsFinished.Should().BeTrue();
        last.Installed.Should().Be(6);
        last.Installing.Should().BeEmpty();
        last.Failed.Should().BeEmpty();
        last.Pending.Should().BeEmpty();
        last.LeftDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Progress_ListsAFailedModWithItsReasonAndSaysTheCollectionWasLeftDisabled()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            ((FomodOption[]?)null, "Broken mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod1.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("broken on purpose");

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        var last = job.JobDefinition.Progress.Current;
        last.IsFinished.Should().BeTrue();
        last.Installed.Should().Be(2);
        last.Failed.Should().ContainSingle();
        last.Failed[0].Name.Should().Be("Broken mod");
        last.Failed[0].Reason.Should().Be("broken on purpose");
        last.Pending.Should().BeEmpty();
        last.LeftDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task Progress_ReportsAlreadyInstalledModsSeparatelyFromThisRunsTotal()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));
        var second = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(second);

        var last = second.JobDefinition.Progress.Current;
        last.AlreadyInstalled.Should().Be(3);
        last.Total.Should().Be(0);
        last.Installed.Should().Be(0);
        last.IsFinished.Should().BeTrue();
    }

    [Fact]
    public async Task TheCollectionsRulesAreKeptOnTheLoadoutAsGroupRules()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        // Mod 0 has to win over mod 1, which the install order would not give it.
        var (file, revision, items) = await CreateCollection(mods, rules: [(0, 1, CollectionDownloadRuleType.After)]);
        var loadout = await CreateLoadout();

        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        var rule = LoadoutGroupRule.FindByLoadout(group.Db, loadout).Should().ContainSingle().Subject;
        rule.Kind.Should().Be(GroupRuleKind.After);
        var nameOf = (LoadoutItemGroupId id) => LoadoutItem.Load(group.Db, id).Name;
        (nameOf(rule.SourceId), nameOf(rule.OtherId)).Should().Be(("Fomod mod 0", "Fomod mod 1"));

        var order = LoadoutItemGroupPriority.FindByLoadout(group.Db, loadout).OrderBy(p => p.Priority).Select(p => nameOf(p.TargetId)).ToList();
        order.Should().Contain(["Fomod mod 0", "Fomod mod 1"]);
        order.IndexOf("Fomod mod 0").Should().BeGreaterThan(order.IndexOf("Fomod mod 1"));
    }

    [Fact]
    public async Task InstallingAgain_LeavesTheEnabledGroupEnabled()
    {
        var mods = Enumerable.Range(0, 2).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var first = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));
        LoadoutItem.Load(Connection.Db, first.Id).IsDisabled.Should().BeFalse("a fully installed collection is enabled");

        // The group no longer carries the disabled marker, so the second run has nothing to retract.
        var second = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        second.Id.Should().Be(first.Id);
        LoadoutItem.Load(Connection.Db, second.Id).IsDisabled.Should().BeFalse();
    }

    private static string[] InstalledFileNames(NexusCollectionLoadoutGroup.ReadOnly group, LoadoutId loadout) => LoadoutItem.FindByLoadout(group.Db, loadout)
        .OfTypeLoadoutItemWithTargetPath()
        .OfTypeLoadoutFile()
        .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
        .Order()
        .ToArray();

    [Fact]
    public async Task Reinstall_ReplacesTheGroup_AndInstallsTheSavedChoicesAgain()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));
        var expected = InstalledFileNames(group, loadout);

        // Simulates an install that went wrong: one mod ended up without its files.
        var brokenMod = group.GetCollectionItems().First();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(brokenMod.Id, recursive: true);
            await tx.Commit();
        }
        // The group handle holds the database it was loaded from, so reload it to see the deletion.
        InstalledFileNames(NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id), loadout).Should().NotEqual(expected);

        var job = await CollectionReinstaller.Reinstall(ServiceProvider, NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id));
        var newGroup = await AwaitJob(job);

        newGroup.Id.Should().NotBe(group.Id);
        NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).IsValid().Should().BeFalse("the old group is removed");
        NexusCollectionLoadoutGroup.All(Connection.Db)
            .Count(g => g.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout)
            .Should().Be(1);
        InstalledFileNames(newGroup, loadout).Should().Equal(expected);
        job.JobDefinition.FailedItems.Should().BeEmpty();

        // Removing the group must leave everything a later reinstall or uninstall needs in the library.
        CollectionRevisionMetadata.Load(Connection.Db, revision.Id).IsValid().Should().BeTrue();
        NexusModsCollectionLibraryFile.Load(Connection.Db, file.Id).IsValid().Should().BeTrue();
        CollectionDownloader.IsFullyDownloaded(items, Connection.Db).Should().BeTrue();
    }

    private int GroupCount(LoadoutId loadout) => NexusCollectionLoadoutGroup.All(Connection.Db)
        .Count(g => g.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout);

    [Fact]
    public async Task Reinstall_WhenAnArchiveIsGoneFromTheLibrary_RefusesAndKeepsTheGroup()
    {
        var mods = Enumerable.Range(0, 2).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        // The archive is no longer found by its hash, as after deleting it from the library.
        items[0].TryGetAsCollectionDownloadExternal(out var external).Should().BeTrue();
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var datom in Connection.Db.Datoms(LibraryFile.Md5, external.Md5))
                tx.Retract(datom.E, LibraryFile.Md5, external.Md5);
            await tx.Commit();
        }

        var calledBack = false;
        var act = async () => await CollectionReinstaller.Reinstall(ServiceProvider, NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id),
            beforeRemove: () => { calledBack = true; return ValueTask.CompletedTask; });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Fomod mod 0*");
        calledBack.Should().BeFalse();
        NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).IsValid().Should().BeTrue();
        GroupCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task Reinstall_WhileAnInstallOfTheCollectionIsRunning_RefusesAndKeepsTheGroup()
    {
        var mods = new (FomodOption[]?, string)[]
        {
            (Choices(), "Good mod 0"),
            (null, "Stale mod"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        // The stale mod is held in the installer, which keeps the install running.
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod1.zip");
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var running = AwaitJob(job);

        try
        {
            await gate.Entered.WaitAsync(Timeout);

            var group = CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db);
            group.HasValue.Should().BeTrue();

            var act = async () => await CollectionReinstaller.Reinstall(ServiceProvider, group.Value);
            await act.Should().ThrowAsync<InvalidOperationException>();
            NexusCollectionLoadoutGroup.Load(Connection.Db, group.Value.Id).IsValid().Should().BeTrue();
        }
        finally
        {
            gate.Release();
        }

        await running;
        GroupCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task Reinstall_OfAGroupThatWasAlreadyRemoved_RefusesAndInstallsNothing()
    {
        var mods = Enumerable.Range(0, 2).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        // A second click, or a second tab of the same collection, holds the handle of the group the first one removed.
        await AwaitJob(await CollectionReinstaller.Reinstall(ServiceProvider, group));
        var newGroup = CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db).Value;

        var act = async () => await CollectionReinstaller.Reinstall(ServiceProvider, group);

        await act.Should().ThrowAsync<InvalidOperationException>();
        GroupCount(loadout).Should().Be(1);
        NexusCollectionLoadoutGroup.Load(Connection.Db, newGroup.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task Reinstall_CallsBeforeRemoveWhileTheOldGroupStillExists()
    {
        var mods = Enumerable.Range(0, 2).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));

        bool? existedDuringCallback = null;
        var job = await CollectionReinstaller.Reinstall(ServiceProvider, group, beforeRemove: () =>
        {
            existedDuringCallback = NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id).IsValid();
            return ValueTask.CompletedTask;
        });
        await AwaitJob(job);

        existedDuringCallback.Should().BeTrue();
    }

    [Fact]
    public async Task Reinstall_InstallsTheOptionalModsThatWereInstalledBefore_AndNoOthers()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, required) = await CreateCollection(mods, optionalIndexes: [1, 2]);
        var loadout = await CreateLoadout();
        var optional = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional);
        var installedOptional = optional.Single(i => i.ArrayIndex == 1);

        var group = await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, [..required, installedOptional]));
        InstalledFileNames(group, loadout).Should().Contain("mod1_a.out.esp").And.NotContain("mod2_a.out.esp");

        var newGroup = await AwaitJob(await CollectionReinstaller.Reinstall(ServiceProvider, NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id)));

        InstalledFileNames(newGroup, loadout).Should().Contain("mod1_a.out.esp").And.NotContain("mod2_a.out.esp");
    }

    [Fact]
    public async Task ValidSavedChoices_InstallWithoutAnyPrompt()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod 0"), (Choices(), "Good mod 1")]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        _window.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task SavedChoicesThatDoNotApply_PromptOnceWithTheMatchingOptionsSelected_AndTheAnswerInstalls()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod 0"), (Choices(secondStepOption: "No such option"), "Mod with stale choices")]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        var pending = job.JobDefinition.PendingItems.Should().ContainSingle().Which;
        pending.Kind.Should().Be(ReviewKind.NeedsChoices);
        _window.Prompts.Should().BeEmpty("the collection install never opens a window");
        PendingRow(group, pending.Download).HasValue.Should().BeTrue();

        var review = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, pending.Download, CancellationToken.None);
        await RunDownload(review);

        var prompt = _window.Prompts.Should().ContainSingle().Which;
        prompt.Notice.Should().Contain("Mod with stale choices").And.Contain("No such option");
        prompt.Steps[0].Groups.Single().Options.Single(o => o.Name == "Option A").Type.Should().Be(OptionType.PreSelected);
        prompt.Steps[0].Groups.Single().Options.Single(o => o.Name == "Option B").Type.Should().Be(OptionType.Available);

        InstalledFileNames(NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id), loadout).Should().Equal("mod0_a.out.esp", "mod0_c.out.esp", "mod1_a.out.esp");
        PendingRow(group, pending.Download).HasValue.Should().BeFalse("installing the mod retracts its row");
    }

    [Fact]
    public async Task TwoModsNeedingPrompts_ArePromptedOneAfterTheOther_WhileAnotherModFinishes()
    {
        var mods = new (FomodOption[]?, string)[]
        {
            (Choices(secondStepOption: "No such option"), "Stale mod 0"),
            (Choices(secondStepOption: "No such option"), "Stale mod 1"),
            (null, "Plain mod"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        _window.Prompts.Should().BeEmpty();
        job.JobDefinition.PendingItems.Should().HaveCount(2);
        job.JobDefinition.FailedItems.Should().BeEmpty();
        job.JobDefinition.Progress.Current.Installed.Should().Be(1);

        // Keep the first window open while the second review waits for it.
        var release = new TaskCompletionSource();
        _window.BeforeAnswer = _ => release.Task;

        var reviews = new List<Task>();
        foreach (var pending in job.JobDefinition.PendingItems)
        {
            var review = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, pending.Download, CancellationToken.None);
            reviews.Add(RunDownload(review));
        }

        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (_window.Prompts.Length < 1)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "a review should open a window");
                await Task.Delay(20);
            }

            // The other stale mod waits for the window instead of opening a second one.
            await Task.Delay(300);
            _window.Prompts.Should().ContainSingle();
        }
        finally
        {
            // The window holds the process-wide gate for guided installers, so a failure above must
            // still close it or every later test that needs a window waits forever.
            release.TrySetResult();
        }

        await Task.WhenAll(reviews).WaitAsync(Timeout);

        _window.Prompts.Should().HaveCount(2);
        _window.MaxConcurrentPrompts.Should().Be(1);
        PendingInstalls.For(Connection.Db, group.Id).Should().BeEmpty();
        InstalledFileNames(NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id), loadout).Should().Contain(["mod0_a.out.esp", "mod1_a.out.esp"]);
    }

    [Fact]
    public async Task CancellingThePrompt_SkipsThatModWithTheReason_AndTheCollectionCompletes()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            (Choices(secondStepOption: "No such option"), "Stale mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        _window.Cancel = true;

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        var pending = job.JobDefinition.PendingItems.Should().ContainSingle().Which;
        pending.Name.Should().Be("Stale mod");
        pending.Reason.Should().Contain("No such option");
        InstalledFileNames(group, loadout).Should().Contain(["mod0_a.out.esp", "mod2_a.out.esp"]).And.NotContain("mod1_a.out.esp");

        // Cancelling the window during the review leaves the mod where it was.
        var review = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, pending.Download, CancellationToken.None);
        var act = () => RunDownload(review);

        await act.Should().ThrowAsync<FomodInstallCancelledException>();
        PendingRow(group, pending.Download).HasValue.Should().BeTrue();
        InstalledFileNames(NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id), loadout).Should().NotContain("mod1_a.out.esp");
    }

    [Fact]
    public async Task AGuidedInstallerWithoutUserInteraction_NeverPrompts_AndLeavesTheModPendingWithTheReason()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod"), (Choices(secondStepOption: "No such option"), "Stale mod")]);
        var loadout = await CreateLoadout();
        _window.SupportsUserInteraction = false;

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        _window.Prompts.Should().BeEmpty();
        job.JobDefinition.FailedItems.Should().BeEmpty();
        var pending = job.JobDefinition.PendingItems.Should().ContainSingle().Which;
        pending.Kind.Should().Be(ReviewKind.NeedsChoices);
        pending.Reason.Should().Contain("No such option");
        InstalledFileNames(group, loadout).Should().Contain("mod0_a.out.esp");
    }

    private async Task<(NexusCollectionLoadoutGroup.ReadOnly Group, LoadoutId Loadout, InstallCollectionJob Job, CollectionRevisionMetadata.ReadOnly Revision, NexusModsCollectionLibraryFile.ReadOnly File, CollectionDownload.ReadOnly[] Items)> InstallWith(
        (FomodOption[]? Choices, string Name)[] mods, ModShape[] shapes, int[]? optionalIndexes = null)
    {
        var (file, revision, items) = await CreateCollection(mods, optionalIndexes, shapes: shapes);
        var loadout = await CreateLoadout();
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);
        return (group, loadout, job.JobDefinition, revision, file, items);
    }

    private static readonly (FomodOption[]? Choices, string Name)[] PlainAndUnknown = [(null, "Plain mod"), (null, "Unknown mod")];

    [Fact]
    public async Task UnplaceableMod_IsRecordedAsPending_AndTheRestInstall()
    {
        var (group, loadout, job, _, _, _) = await InstallWith(
            [(Choices(), "Fomod mod 0"), (null, "Unknown mod"), (Choices(), "Fomod mod 2")],
            [ModShape.Fomod, ModShape.Unknown, ModShape.Fomod]);

        job.FailedItems.Should().BeEmpty();
        var pending = job.PendingItems.Should().ContainSingle().Which;
        pending.Kind.Should().Be(ReviewKind.NoInstaller);
        pending.Name.Should().Be("Unknown mod");
        InstalledFileNames(group, loadout).Should().Contain(["mod0_a.out.esp", "mod2_a.out.esp"]);

        var rows = PendingInstalls.For(Connection.Db, group.Id);
        rows.Should().ContainSingle().Which.Download.Id.Should().Be(pending.Download.Id);
        CollectionDownloader.GetStatus(pending.Download, group.AsCollectionGroup(), Connection.Db).IsInLibrary(out _).Should().BeTrue();
    }

    [Fact]
    public async Task PlainFomodWithSteps_IsRecordedAsPending_WithNoPrompt()
    {
        var (group, loadout, job, _, _, _) = await InstallWith([(null, "Stepped")], [ModShape.Fomod]);

        job.PendingItems.Should().ContainSingle().Which.Kind.Should().Be(ReviewKind.NeedsChoices);
        job.FailedItems.Should().BeEmpty();
        _window.Prompts.Should().BeEmpty();
        InstalledFileNames(group, loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task FomodWithoutSteps_InstallsUnattended()
    {
        var (group, loadout, job, _, _, _) = await InstallWith([(null, "Stepless")], [ModShape.FomodWithoutSteps]);

        job.FailedItems.Should().BeEmpty();
        job.PendingItems.Should().BeEmpty();
        InstalledFileNames(group, loadout).Should().Contain("mod0_required.out.esp");
        PendingInstalls.For(Connection.Db, group.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task UnplaceableMod_NeverRunsTheManualInstaller()
    {
        await InstallWith(PlainAndUnknown, [ModShape.Plain, ModShape.Unknown]);

        _manualInstaller.Calls.Should().Be(0);
    }

    [Fact]
    public async Task UnplaceableMod_LeavesTheCollectionEnabled_WhenNothingFailed()
    {
        var (group, _, job, _, _, _) = await InstallWith(PlainAndUnknown, [ModShape.Plain, ModShape.Unknown]);

        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeFalse();
        job.Progress.Current.LeftDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task AHardFailureStillDisablesTheCollection_WithAPendingModBesideIt()
    {
        var (file, revision, items) = await CreateCollection([(null, "Plain mod"), (null, "Unknown mod")], shapes: [ModShape.Plain, ModShape.Unknown]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod0.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("broken on purpose");

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeTrue();
        job.JobDefinition.Progress.Current.Failed.Should().ContainSingle();
        job.JobDefinition.Progress.Current.Pending.Should().ContainSingle();
        job.JobDefinition.Progress.Current.LeftDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task InstallingOnlyOptionalMods_KeepsTheGroupEnabled_WhenRequiredModsArePending()
    {
        var (group, loadout, job, revision, file, _) = await InstallWith(PlainAndUnknown.Reverse().ToArray(), [ModShape.Unknown, ModShape.Plain], optionalIndexes: [1]);
        job.PendingItems.Should().ContainSingle();
        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeFalse();

        var optional = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional);
        await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, optional));

        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeFalse("a required mod waiting for review is not a missing install");
    }

    [Fact]
    public async Task InstallingAgain_RetriesThePendingMod_AndKeepsOneRow()
    {
        var (group, loadout, job, revision, file, items) = await InstallWith(PlainAndUnknown, [ModShape.Plain, ModShape.Unknown]);
        job.PendingItems.Should().ContainSingle();

        var second = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(second);

        second.JobDefinition.PendingItems.Should().ContainSingle();
        PendingInstalls.For(Connection.Db, group.Id).Should().ContainSingle();

        var installers = Game.LibraryItemInstallers;
        var original = installers[1];
        installers[1] = new ClaimingInstaller("mod1.zip");
        try
        {
            var third = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
            await AwaitJob(third);

            third.JobDefinition.PendingItems.Should().BeEmpty();
            third.JobDefinition.FailedItems.Should().BeEmpty();
        }
        finally
        {
            installers[1] = original;
        }

        PendingInstalls.For(Connection.Db, group.Id).Should().BeEmpty();
        Child(NexusCollectionLoadoutGroup.Load(Connection.Db, group.Id), "mod1.zip").Should().NotBeNull();
    }

    [Fact]
    public async Task InstallingOneOptionalFromTheCollectionPage_RecordsItAsPending_WithNoDialog()
    {
        var (group, loadout, _, revision, _, _) = await InstallWith(
            [(Choices(), "Fomod mod"), (null, "Unknown optional")], [ModShape.Fomod, ModShape.Unknown], optionalIndexes: [1]);
        var optional = CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Optional).Single();

        var download = await InstallCollectionDownloadJob.Create(ServiceProvider, loadout, optional, CancellationToken.None, unattended: true);
        var act = () => RunDownload(download);

        (await act.Should().ThrowAsync<ReviewRequiredException>()).Which.Kind.Should().Be(ReviewKind.NoInstaller);
        PendingRow(group, optional).HasValue.Should().BeTrue();
        _manualInstaller.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CancellingTheInstall_KeepsThePendingRowsAlreadyRecorded()
    {
        var (file, revision, items) = await CreateCollection([(null, "Unknown mod"), (null, "Plain mod")], shapes: [ModShape.Unknown, ModShape.Plain]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod1.zip");

        var unknown = items.Single(item => item.ArrayIndex == 0);
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var running = AwaitJob(job);

        await gate.Entered.WaitAsync(Timeout);
        var deadline = DateTime.UtcNow + Timeout;
        while (!PendingInstalls.Find(Connection.Db, CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db).Value.Id, CollectionDownloadId.From(unknown.Id)).HasValue)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the unknown mod should be recorded while the plain mod is held");
            await Task.Delay(20);
        }

        ServiceProvider.GetRequiredService<IJobMonitor>().Cancel(job);
        gate.Fail(new OperationCanceledException());
        var act = async () => await running;
        await act.Should().ThrowAsync<Exception>();

        var group = CollectionDownloader.GetCollectionGroup(revision, loadout, Connection.Db).Value;
        PendingInstalls.For(Connection.Db, group.Id).Should().ContainSingle().Which.Download.Id.Should().Be(unknown.Id);
        LoadoutItem.Load(Connection.Db, group.Id).IsDisabled.Should().BeTrue("the install never finished, so the collection stays off");
    }

    [Fact]
    public async Task AModInstalledIntoMyMods_LeavesThePendingRow_AndItsStatusInLibrary()
    {
        var (group, loadout, job, _, _, _) = await InstallWith(PlainAndUnknown, [ModShape.Plain, ModShape.Unknown]);
        var pending = job.PendingItems.Should().ContainSingle().Which;
        CollectionDownloader.GetStatus(pending.Download, group.AsCollectionGroup(), Connection.Db).IsInLibrary(out var libraryItem).Should().BeTrue();

        await LoadoutManager.InstallItem(libraryItem, loadout, installer: new ClaimingInstaller("mod1.zip"));

        PendingRow(group, pending.Download).HasValue.Should().BeTrue();
        CollectionDownloader.GetStatus(pending.Download, group.AsCollectionGroup(), Connection.Db).IsInLibrary(out _).Should().BeTrue();
    }

    [Fact]
    public async Task AGameWithoutAFomodInstaller_LandsASavedChoicesModAsPending_NotFailed()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Fomod mod")]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 0, "none.zip"); // hides FomodXmlInstaller from OfType

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        var pending = job.JobDefinition.PendingItems.Should().ContainSingle().Which;
        pending.Kind.Should().Be(ReviewKind.NeedsChoices);
        pending.Reason.Should().Be("the game has no FOMOD installer");
    }

    [Fact]
    public async Task Progress_ListsPendingModsSeparatelyFromFailures()
    {
        var (file, revision, items) = await CreateCollection([(null, "Unknown mod"), (null, "Broken mod")], shapes: [ModShape.Unknown, ModShape.Plain]);
        var loadout = await CreateLoadout();
        using var gate = ArchiveGate.Install(Game.LibraryItemInstallers, 1, "mod1.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("broken on purpose");

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        var last = job.JobDefinition.Progress.Current;
        last.Pending.Should().ContainSingle().Which.Name.Should().Be("Unknown mod");
        last.Failed.Should().ContainSingle().Which.Name.Should().Be("Broken mod");
    }
}
