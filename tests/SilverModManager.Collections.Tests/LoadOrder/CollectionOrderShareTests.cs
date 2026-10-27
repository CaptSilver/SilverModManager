using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

/// <summary>What sharing a collection writes into its manifest, and that a fresh install of that manifest gets the same order and rules back.</summary>
public class CollectionOrderShareTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionOrderShareTests>(helper)
{
    private static readonly GameDomain Domain = GameDomain.From("cyberpunk2077");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private record Pack(Loadout.ReadOnly Loadout, LoadoutItemGroupId Container, IReadOnlyDictionary<string, LoadoutItemGroupId> Mods);

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    /// <summary>A "Pack" group holding one Nexus-linked child per entry; the priority is the child's, higher wins.</summary>
    private async Task<Pack> BuildNexusPack(params (string Name, ulong Priority)[] mods)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var children = new Dictionary<string, LoadoutItemGroupId>();
        for (var i = 0; i < mods.Length; i++)
        {
            var item = ShareableMods.AddNexusItem(tx, mods[i].Name, Cyberpunk2077Game.NexusModsGameId.Value, Domain, modId: (uint)(100 + i), fileId: (uint)(1000 + i));
            var group = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, mods[i].Name, item);
            ShareableMods.SetPriority(tx, loadout.LoadoutId, group, mods[i].Priority);
            children[mods[i].Name] = group;
        }
        var result = await tx.Commit();
        return new Pack(loadout, LoadoutItemGroupId.From(result[container].Value),
            children.ToDictionary(pair => pair.Key, pair => LoadoutItemGroupId.From(result[pair.Value].Value)));
    }

    private async Task AddRule(Pack pack, string source, string other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutGroupRule.New(tx)
        {
            LoadoutId = pack.Loadout.LoadoutId,
            SourceId = pack.Mods[source],
            OtherId = pack.Mods[other],
            Kind = kind,
        };
        await tx.Commit();
    }

    private async Task<User.ReadOnly> Author()
    {
        using var tx = Connection.BeginTransaction();
        var author = new User.New(tx) { Name = "Sharer", NexusId = 99 };
        var result = await tx.Commit();
        return User.Load(result.Db, result[author.Id]);
    }

    private async Task<CollectionRoot> Manifest(Pack pack) => CollectionCreator.LoadoutItemGroupToCollectionManifest(
        LoadoutItemGroup.Load(Connection.Db, pack.Container),
        new FixedGameDomains(Domain, Cyberpunk2077Game.NexusModsGameId.Value),
        await Author(),
        VanityVersion.From("1.0.0"));

    private static string TagOf(CollectionRoot manifest, string name) => manifest.Mods.Single(mod => mod.Name == name).Source.Tag!;

    [Fact]
    public async Task TheKeyListsModsWinnerFirstWithTheirSwitches()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 3), ("C", 2));
        await DisableItem(pack.Mods["C"].Value);

        var manifest = await Manifest(pack);

        manifest.SilverLoadOrder.Should().NotBeNull();
        manifest.SilverLoadOrder!.Select(entry => (entry.Name, entry.Enabled)).Should().Equal(("B", true), ("C", false), ("A", true));
        manifest.SilverLoadOrder.Select(entry => (entry.ModId, entry.FileId)).Should().OnlyContain(ids => ids.ModId != null && ids.FileId != null);
    }

    [Fact]
    public async Task EveryModSourceCarriesATagAndRulesReferenceIt()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 2));
        await AddRule(pack, "B", "A", GroupRuleKind.After);

        var manifest = await Manifest(pack);

        var tags = manifest.Mods.Select(mod => mod.Source.Tag).ToArray();
        tags.Should().OnlyContain(tag => !string.IsNullOrWhiteSpace(tag)).And.OnlyHaveUniqueItems();
        var rule = manifest.ModRules.Should().ContainSingle().Subject;
        rule.Source.Tag.Should().Be(TagOf(manifest, "B"));
        rule.Other.Tag.Should().Be(TagOf(manifest, "A"));
    }

    [Fact]
    public async Task StoredRulesBetweenTheCollectionsModsAreWrittenAsModRules()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 2), ("C", 3));
        await AddRule(pack, "B", "A", GroupRuleKind.After);
        await AddRule(pack, "A", "C", GroupRuleKind.Before);

        var manifest = await Manifest(pack);

        manifest.ModRules.Select(rule => (rule.Type, rule.Source.Tag, rule.Other.Tag)).Should().BeEquivalentTo(
        [
            (VortexModRuleType.After, TagOf(manifest, "B"), TagOf(manifest, "A")),
            (VortexModRuleType.Before, TagOf(manifest, "A"), TagOf(manifest, "C")),
        ]);
    }

    [Fact]
    public async Task ARuleWithoutALocalFileCarriesNoMd5ButARuleOnADownloadedModDoes()
    {
        var loadout = await CreateLoadout();
        var md5 = Md5Value.From((UInt128)777);
        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var file = ShareableMods.AddDownloadedFile(tx, "downloaded.zip", md5);
        var downloaded = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, "Downloaded", file);
        var nexus = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, "Nexus",
            ShareableMods.AddNexusItem(tx, "Nexus", Cyberpunk2077Game.NexusModsGameId.Value, Domain, 5, 50));
        _ = new LoadoutGroupRule.New(tx) { LoadoutId = loadout.LoadoutId, SourceId = downloaded, OtherId = nexus, Kind = GroupRuleKind.After };
        var result = await tx.Commit();

        var manifest = await Manifest(new Pack(loadout, LoadoutItemGroupId.From(result[container].Value), new Dictionary<string, LoadoutItemGroupId>()));

        var rule = manifest.ModRules.Should().ContainSingle().Subject;
        rule.Source.FileMD5.Should().Be(md5);
        rule.Other.FileMD5.Should().Be(default(Md5Value));
        rule.Other.Tag.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RulesInvolvingAModOutsideTheCollectionAreNotWritten()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 2));
        using (var tx = Connection.BeginTransaction())
        {
            var outside = AddEmptyGroup(tx, pack.Loadout.LoadoutId, "Outside");
            _ = new LoadoutGroupRule.New(tx) { LoadoutId = pack.Loadout.LoadoutId, SourceId = pack.Mods["A"], OtherId = outside, Kind = GroupRuleKind.After };
            await tx.Commit();
        }

        var manifest = await Manifest(pack);

        manifest.ModRules.Should().BeEmpty();
    }

    [Fact]
    public async Task AChildWithNoNexusFileOrDownloadIsLeftOutOfTheKeyAndReported()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 2));
        LoadoutItemGroupId plain;
        using (var tx = Connection.BeginTransaction())
        {
            // A library file that came from neither Nexus nor a download page (a local import) cannot be named by a manifest.
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = "local.zip",
                Hash = "local".xxHash3AsUtf8(),
                Size = Size.FromLong(1),
                LibraryItem = new LibraryItem.New(tx, id) { Name = "local.zip" },
            };
            var group = ShareableMods.AddLinkedGroup(tx, pack.Loadout.LoadoutId, pack.Container, "Local", file.Id);
            ShareableMods.SetPriority(tx, pack.Loadout.LoadoutId, group, 9);
            _ = new LoadoutGroupRule.New(tx) { LoadoutId = pack.Loadout.LoadoutId, SourceId = group, OtherId = pack.Mods["A"], Kind = GroupRuleKind.After };
            var result = await tx.Commit();
            plain = LoadoutItemGroupId.From(result[group].Value);
        }

        var shared = CollectionCreator.BuildManifest(
            LoadoutItemGroup.Load(Connection.Db, pack.Container),
            new FixedGameDomains(Domain, Cyberpunk2077Game.NexusModsGameId.Value),
            await Author(),
            VanityVersion.From("1.0.0"));

        shared.Manifest.Mods.Select(mod => mod.Name).Should().BeEquivalentTo("A", "B");
        shared.Manifest.SilverLoadOrder!.Select(entry => entry.Name).Should().Equal("B", "A");
        shared.Manifest.ModRules.Should().BeEmpty();
        shared.Skipped.Select(group => group.LoadoutItemGroupId).Should().Equal(plain);
    }

    [Fact]
    public async Task AGroupWithNoShareableChildrenWritesNoKeyAtAll()
    {
        var pack = await BuildNexusPack();

        var manifest = await Manifest(pack);

        manifest.SilverLoadOrder.Should().BeNull("an empty array counts as present on install and would hide a Vortex key");
        JsonSerializer.Serialize(manifest, ServiceProvider.GetRequiredService<JsonSerializerOptions>()).Should().NotContain("silverLoadOrder");
    }

    [Fact]
    public void TheRuleReferenceSerialisesItsTagAsVortexSpellsIt()
    {
        var rule = new ModRule
        {
            Type = VortexModRuleType.After,
            Source = new VortexModReference { Tag = "one" },
            Other = new VortexModReference { Tag = "two" },
        };
        var options = ServiceProvider.GetRequiredService<JsonSerializerOptions>();

        var text = JsonSerializer.Serialize(rule, options);
        var back = JsonSerializer.Deserialize<ModRule>(text, options)!;

        text.Should().Contain("\"tag\"").And.NotContain("\"Tag\"");
        (back.Source.Tag, back.Other.Tag).Should().Be(("one", "two"));
    }

    private sealed class RecordingFormat(string key, JsonElement? value) : IVortexOrderFormat
    {
        public IReadOnlyList<string> Written { get; private set; } = [];
        public string Key => key;
        public (IReadOnlyList<ModOrderSourceEntry> Entries, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement _, CollectionRoot __) => ([], []);

        public JsonElement? Write(Loadout.ReadOnly loadout, IReadOnlyList<LoadoutItemGroup.ReadOnly> winnerToLoser)
        {
            Written = winnerToLoser.Select(group => group.AsLoadoutItem().Name).ToArray();
            return value;
        }
    }

    private sealed class OrderedGame(IServiceProvider provider, IVortexOrderFormat format) : StubbedGame(provider), IModOrderGame
    {
        public IModOrderSink? OrderSink => null;
        public IVortexOrderFormat? VortexOrderFormat => format;
    }

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroup.ReadOnly[] Groups, CollectionRoot Root)> VortexSetup()
    {
        var pack = await BuildNexusPack(("A", 1), ("B", 2));
        var groups = new[] { "B", "A" }.Select(name => LoadoutItemGroup.Load(Connection.Db, pack.Mods[name])).ToArray();
        var root = await Manifest(pack);
        return (pack.Loadout, groups, root);
    }

    [Fact]
    public async Task TheGamesVortexKeyIsWrittenFromTheOrderedGroups()
    {
        var (loadout, groups, root) = await VortexSetup();
        var value = JsonDocument.Parse("""["B","A"]""").RootElement.Clone();
        var format = new RecordingFormat("loadOrder", value);

        var written = CollectionCreator.WithVortexOrder(root, new OrderedGame(ServiceProvider, format), loadout, groups);

        format.Written.Should().Equal("B", "A");
        written.LoadOrder.Should().NotBeNull();
        written.LoadOrder!.Value.GetRawText().Should().Be("""["B","A"]""");
        written.Mods.Should().BeSameAs(root.Mods);
        written.SilverLoadOrder.Should().BeSameAs(root.SilverLoadOrder);
    }

    [Fact]
    public async Task AGameWithNoVortexFormatOrOneThatWritesNothingLeavesTheKeyOut()
    {
        var (loadout, groups, root) = await VortexSetup();

        CollectionCreator.WithVortexOrder(root, GameInstallation.GetGame(), loadout, groups).LoadOrder.Should().BeNull();
        CollectionCreator.WithVortexOrder(root, new OrderedGame(ServiceProvider, new RecordingFormat("loadOrder", null)), loadout, groups)
            .LoadOrder.Should().BeNull();
    }

    [Fact]
    public async Task AFormatWithAKeyTheAppDoesNotKnowIsRefusedRatherThanWrittenNowhere()
    {
        var (loadout, groups, root) = await VortexSetup();
        var value = JsonDocument.Parse("[]").RootElement.Clone();

        var act = () => CollectionCreator.WithVortexOrder(root, new OrderedGame(ServiceProvider, new RecordingFormat("bogus", value)), loadout, groups);

        act.Should().Throw<ArgumentException>().WithMessage("*bogus*");
    }

    /// <summary>Plain mods whose archives are real files in the library, each marked as downloaded from a page so a manifest can name it by md5.</summary>
    private async Task<(Pack Pack, Dictionary<string, LoadoutItemGroupId> Groups)> BuildInstallablePack(params (int Index, ulong Priority)[] mods)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var loadout = await CreateLoadout();
        var files = new Dictionary<int, EntityId>();
        foreach (var (index, _) in mods)
        {
            var (path, md5) = await Offline.CreateFomodMod(folder.Path, index, plain: true);
            var file = await LibraryService.AddLocalFile(path);
            using var fileTx = Connection.BeginTransaction();
            fileTx.Add(file.Id, LibraryFile.Md5, md5);
            fileTx.Add(file.Id, DirectDownloadLibraryFile.LogicalFileName, path.FileName);
            fileTx.Add(file.Id, DownloadedFile.DownloadPageUri, new Uri($"https://example.invalid/mod{index}"));
            await fileTx.Commit();
            files[index] = file.Id;
        }

        using var tx = Connection.BeginTransaction();
        var container = AddEmptyGroup(tx, loadout.LoadoutId, "Pack");
        var groups = new Dictionary<string, LoadoutItemGroupId>();
        foreach (var (index, priority) in mods)
        {
            var group = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, container, $"mod{index}.zip", files[index]);
            ShareableMods.SetPriority(tx, loadout.LoadoutId, group, priority);
            groups[$"mod{index}.zip"] = group;
        }
        var result = await tx.Commit();
        var resolved = groups.ToDictionary(pair => pair.Key, pair => LoadoutItemGroupId.From(result[pair.Value].Value));
        return (new Pack(loadout, LoadoutItemGroupId.From(result[container].Value), resolved), resolved);
    }

    private async Task<NexusCollectionLoadoutGroup.ReadOnly> InstallOnFreshLoadout(CollectionRoot manifest, LoadoutId into)
    {
        var (file, revision, items) = await Offline.Create(manifest);
        var job = InstallCollectionJob.Create(ServiceProvider, into, file, revision, items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    private string[] ByPriority(NexusCollectionLoadoutGroup.ReadOnly group, LoadoutId loadout)
    {
        var db = Connection.Db;
        var priorities = LoadoutItemGroupPriority.FindByLoadout(db, loadout).ToDictionary(row => row.TargetId, row => row.Priority.Value);
        return LoadoutItemGroup.Load(db, group.Id).Children
            .OfTypeLoadoutItemGroup()
            .OrderByDescending(child => priorities[child.LoadoutItemGroupId])
            .Select(child => child.AsLoadoutItem().Name)
            .ToArray();
    }

    [Fact]
    public async Task RoundTripRestoresTheOrderOnAFreshLoadout_RedMod()
    {
        var (pack, groups) = await BuildInstallablePack((0, 1), (1, 3), (2, 2));
        await DisableItem(groups["mod2.zip"].Value);
        var manifest = await Manifest(pack);

        var fresh = await CreateLoadout();
        var installed = await InstallOnFreshLoadout(manifest, fresh.LoadoutId);

        ByPriority(installed, fresh.LoadoutId).Should().Equal("mod1.zip", "mod2.zip", "mod0.zip");
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, fresh.LoadoutId).Should().BeEmpty();

        var disabled = LoadoutItemGroup.Load(Connection.Db, installed.Id).Children.OfTypeLoadoutItemGroup()
            .Where(child => child.AsLoadoutItem().IsDisabled).Select(child => child.AsLoadoutItem().Name);
        disabled.Should().Equal("mod2.zip");

        var variety = GameInstallation.GetGame().SortOrderManager.GetSortOrderVarieties().OfType<RedModSortOrderVariety>().Single();
        var sortOrder = variety.GetSortOrderIdFor(fresh.LoadoutId, Connection.Db).Value;
        variety.GetSortOrderItems(sortOrder, Connection.Db).OrderBy(item => item.SortIndex).Select(item => item.Key.Key)
            .Should().Equal("plain1", "plain2", "plain0");
    }

    [Fact]
    public async Task RoundTripRestoresTheStoredRulesAsRuleRows()
    {
        var (pack, groups) = await BuildInstallablePack((0, 1), (1, 3), (2, 2));
        // Both agree with the order (1 over 2 over 0), so neither is reported as overridden.
        await AddRule(pack, groups, "mod1.zip", "mod2.zip", GroupRuleKind.After);
        await AddRule(pack, groups, "mod0.zip", "mod2.zip", GroupRuleKind.Before);
        var manifest = await Manifest(pack);

        var fresh = await CreateLoadout();
        await InstallOnFreshLoadout(manifest, fresh.LoadoutId);

        var db = Connection.Db;
        var nameOf = (LoadoutItemGroupId id) => LoadoutItem.Load(db, id).Name;
        LoadoutGroupRule.FindByLoadout(db, fresh.LoadoutId)
            .Select(rule => (nameOf(rule.SourceId), nameOf(rule.OtherId), rule.Kind))
            .Should().BeEquivalentTo(
            [
                ("mod1.zip", "mod2.zip", GroupRuleKind.After),
                ("mod0.zip", "mod2.zip", GroupRuleKind.Before),
            ]);
    }

    private async Task AddRule(Pack pack, Dictionary<string, LoadoutItemGroupId> groups, string source, string other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutGroupRule.New(tx) { LoadoutId = pack.Loadout.LoadoutId, SourceId = groups[source], OtherId = groups[other], Kind = kind };
        await tx.Commit();
    }

    [Fact]
    public async Task RulesOnModsWithNoLocalFileAreMatchedToTheirDownloadsByTag()
    {
        // No md5 anywhere, so the tag is the only thing the install side can match a rule on.
        var pack = await BuildNexusPack(("A", 1), ("B", 2));
        await AddRule(pack, "B", "A", GroupRuleKind.After);
        var manifest = await Manifest(pack);

        await Offline.Create(manifest);

        var db = Connection.Db;
        var rule = Enumerable.Range(0, 3).SelectMany(i => CollectionDownloadRules.FindByArrayIndex(db, i)).Should().ContainSingle().Subject;
        (CollectionDownload.Load(db, rule.SourceId).Name, CollectionDownload.Load(db, rule.OtherId).Name, rule.RuleType)
            .Should().Be(("B", "A", CollectionDownloadRuleType.After));
    }
}
