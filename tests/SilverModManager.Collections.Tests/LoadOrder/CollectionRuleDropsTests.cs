using DynamicData.Kernel;
using NexusMods.Paths;
using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Xunit;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Collections.Tests.LoadOrder;

/// <summary>The collection's before/after rules that cannot order anything, and that they reach the Health Check through a real install.</summary>
public class CollectionRuleDropsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionRuleDropsTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    private static VortexModReference Tagged(string tag) => new() { Tag = tag };

    private static ModRule Rule(VortexModRuleType type, string source, string other) =>
        new() { Type = type, Source = Tagged(source), Other = Tagged(other) };

    /// <summary>A collection of plain mods tagged "tag0", "tag1", ... whose archives are already in the library.</summary>
    private async Task<CollectionRoot> Manifest(int modCount, params ModRule[] rules)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var offline = Offline;
        var mods = new List<Mod>();
        for (var i = 0; i < modCount; i++)
        {
            var (path, md5) = await offline.CreateFomodMod(folder.Path, i, plain: true);
            await offline.AddDirectDownload(path, md5);
            mods.Add(new Mod
            {
                Name = $"Mod {i}",
                DomainName = GameDomain.From("cyberpunk2077"),
                Source = new ModSource
                {
                    Type = ModSourceType.Direct,
                    Md5 = md5,
                    Url = new Uri($"https://example.invalid/mod{i}.zip"),
                    LogicalFilename = $"mod{i}.zip",
                    FileSize = Size.From((ulong)path.FileInfo.Size),
                    Tag = $"tag{i}",
                },
            });
        }

        return new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods.ToArray(),
            ModRules = rules,
        };
    }

    private async Task<NexusCollectionLoadoutGroup.ReadOnly> Install(
        (NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items) collection, LoadoutId loadout)
    {
        var job = InstallCollectionJob.Create(ServiceProvider, loadout, collection.File, collection.Revision, collection.Items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    private static Optional<CollectionGroup.ReadOnly> NoGroup => default;

    [Fact]
    public async Task AnOrderRuleNamingNoDownloadComesBackAsADrop()
    {
        var manifest = await Manifest(2, Rule(VortexModRuleType.Before, "tag0", "ghost"));
        var (_, revision, _) = await Offline.Create(manifest);

        var drops = CollectionRuleDrops.Read(Connection.Db, manifest, revision, NoGroup);

        drops.Should().Equal(new ModOrderDrop("rule 'tag0 before ghost'", "it names a mod that is not in the collection"));
    }

    [Fact]
    public async Task ARuleIsDescribedByTheFirstNameItCarries()
    {
        var manifest = await Manifest(1, new ModRule
        {
            Type = VortexModRuleType.After,
            Source = new VortexModReference { LogicalFileName = "Logical.zip", FileExpression = "expr*", Tag = "t" },
            Other = new VortexModReference { FileExpression = "Other*" },
        }, new ModRule { Type = VortexModRuleType.After, Source = new VortexModReference(), Other = new VortexModReference { FileMD5 = Md5Value.From((UInt128)255) } });
        var (_, revision, _) = await Offline.Create(manifest);

        var items = CollectionRuleDrops.Read(Connection.Db, manifest, revision, NoGroup).Select(drop => drop.Item).ToArray();

        items.Should().Equal("rule 'Logical.zip after Other*'", $"rule 'an unnamed mod after {Md5Value.From((UInt128)255)}'");
    }

    [Fact]
    public async Task NonOrderRulesAreNotReported()
    {
        var manifest = await Manifest(2, Rule(VortexModRuleType.Requires, "tag0", "ghost"), Rule(VortexModRuleType.Conflicts, "ghost", "tag1"));
        var (_, revision, _) = await Offline.Create(manifest);

        CollectionRuleDrops.Read(Connection.Db, manifest, revision, NoGroup).Should().BeEmpty();
    }

    [Fact]
    public async Task ARuleBetweenTwoCollectionModsIsNotReported()
    {
        var manifest = await Manifest(2, Rule(VortexModRuleType.Before, "tag0", "tag1"));
        var collection = await Offline.Create(manifest);
        var group = await Install(collection, (await CreateLoadout()).LoadoutId);

        CollectionRuleDrops.Read(Connection.Db, manifest, collection.Revision, group.AsCollectionGroup()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARuleOnAnOptionalModThatWasNotInstalledIsReportedAsNotInstalled()
    {
        var manifest = await Manifest(2, Rule(VortexModRuleType.After, "tag0", "tag1"));
        var collection = await Offline.Create(manifest, optionalIndexes: [1]);
        var group = await Install(collection, (await CreateLoadout()).LoadoutId);

        var drops = CollectionRuleDrops.Read(Connection.Db, manifest, collection.Revision, group.AsCollectionGroup());

        drops.Should().Equal(new ModOrderDrop("rule 'tag0 after tag1'", "one of its mods is not installed"));
    }

    [Fact]
    public async Task ACollectionWithNoOrderKeyStillRecordsRuleDrops()
    {
        var manifest = await Manifest(2, Rule(VortexModRuleType.Before, "tag0", "ghost"));
        manifest.SilverLoadOrder.Should().BeNull();
        manifest.LoadOrder.Should().BeNull();
        var loadout = await CreateLoadout();

        var group = await Install(await Offline.Create(manifest), loadout.LoadoutId);

        var row = ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        row.ContainerId.Should().Be(group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId);
        row.Item.Should().Be("rule 'tag0 before ghost'");
        row.Reason.Should().Be("it names a mod that is not in the collection");
        row.Page.Should().Be("Load Order");
        row.Source.Should().Be("collection Offline");
    }

    [Fact]
    public async Task InstallingAgainOnceTheRuleResolvesClearsTheRuleDrop()
    {
        var broken = await Manifest(2, Rule(VortexModRuleType.Before, "tag0", "ghost"));
        var loadout = await CreateLoadout();
        var collection = await Offline.Create(broken);
        await Install(collection, loadout.LoadoutId);
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();

        // What fetching a corrected revision stores: the rule now matches two downloads.
        using (var tx = Connection.BeginTransaction())
        {
            var corrected = new CollectionRoot { Info = broken.Info, Mods = broken.Mods, ModRules = [Rule(VortexModRuleType.Before, "tag0", "tag1")] };
            SilverModManager.Networking.NexusWebApi.NexusModsLibrary.AddCollectionDownloadRules(tx, corrected, collection.Revision);
            await tx.Commit();
        }
        await Install(collection, loadout.LoadoutId);

        ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }
}
