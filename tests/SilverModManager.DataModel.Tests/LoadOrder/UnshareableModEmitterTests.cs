using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.DataModel.Tests.LoadOrder;

public class UnshareableModEmitterTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<UnshareableModEmitterTests>(helper)
{
    private static readonly GameDomain Domain = GameDomain.From("cyberpunk2077");

    private record Built(Loadout.ReadOnly Loadout, LoadoutItemGroupId Collection, LoadoutItemGroupId Nexus, LoadoutItemGroupId Plain, LoadoutItemGroupId Outside);

    /// <summary>A collection holding one Nexus-linked child ("Nexus") and one plain local archive ("Plain"), plus a group outside it.</summary>
    private async Task<Built> Build(bool managed = true)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();

        var author = new User.New(tx) { Name = "author", NexusId = 1 };
        var metadata = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(1),
            Slug = CollectionSlug.From("slug-1"),
            Name = "Shared pack",
            GameId = Cyberpunk2077Game.NexusModsGameId.Value,
            AuthorId = author.Id,
        };

        var collection = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = "Shared pack", LoadoutId = loadout.LoadoutId },
            },
        };
        if (managed)
        {
            _ = new ManagedCollectionLoadoutGroup.New(tx, id)
            {
                CollectionGroup = collection,
                CollectionId = metadata.Id,
                CurrentRevisionNumber = RevisionNumber.From(1),
                LastUploadDate = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
        }
        var parent = LoadoutItemGroupId.From(id.Value);

        var nexus = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, parent, "Nexus mod",
            ShareableMods.AddNexusItem(tx, "Nexus mod", Cyberpunk2077Game.NexusModsGameId.Value, Domain, 5, 50));

        // A file that came from neither Nexus nor a download page (a local import) cannot be named by a manifest.
        var local = new LibraryFile.New(tx, out var localId)
        {
            FileName = "local.zip",
            Hash = "local".xxHash3AsUtf8(),
            Size = Size.FromLong(1),
            LibraryItem = new LibraryItem.New(tx, localId) { Name = "local.zip" },
        };
        var plain = ShareableMods.AddLinkedGroup(tx, loadout.LoadoutId, parent, "Plain archive", local.Id);

        var outside = AddEmptyGroup(tx, loadout.LoadoutId, "Outside mod");
        var result = await tx.Commit();

        LoadoutItemGroupId Map(LoadoutItemGroupId group) => LoadoutItemGroupId.From(result[group.Value].Value);
        return new Built(loadout, Map(parent), Map(nexus), Map(plain), Map(outside));
    }

    private async Task AddRule(Built built, LoadoutItemGroupId source, LoadoutItemGroupId other)
    {
        using var tx = Connection.BeginTransaction();
        _ = new LoadoutGroupRule.New(tx) { LoadoutId = built.Loadout.LoadoutId, SourceId = source, OtherId = other, Kind = GroupRuleKind.After };
        await tx.Commit();
    }

    private async Task<Diagnostic[]> Cards(Built built)
    {
        var emitter = new UnshareableModEmitter();
        var result = new List<Diagnostic>();
        var loadout = Loadout.Load(Connection.Db, built.Loadout.LoadoutId);
        await foreach (var diagnostic in emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            result.Add(diagnostic);
        return result.ToArray();
    }

    private static string Text(Diagnostic diagnostic) =>
        diagnostic.FormatSummary(new PlainDiagnosticWriter()) + " | " + diagnostic.FormatDetails(new PlainDiagnosticWriter());

    private async Task<string[]> Summaries(Built built) => (await Cards(built)).Select(Text).ToArray();

    private async Task<Diagnostic> RuleCard(Built built, string ruleText) =>
        (await Cards(built)).Should().ContainSingle(card => Text(card).Contains(ruleText)).Subject;

    [Fact]
    public async Task AChildWithNoNexusFileOrDownloadGetsACard()
    {
        var built = await Build();

        var cards = await Summaries(built);

        var card = cards.Should().ContainSingle().Subject;
        card.Should().Contain("Plain archive").And.Contain("collection Shared pack").And.Contain("Library page");
        card.Should().Contain("it has no Nexus file or download the collection could name, so sharing leaves it out");
    }

    [Fact]
    public async Task AnOrdinaryCollectionGroupRaisesNothing()
    {
        var built = await Build(managed: false);

        (await Summaries(built)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARuleToAChildThatCannotBeNamedGetsACard()
    {
        var built = await Build();
        await AddRule(built, built.Nexus, built.Plain);

        var cards = await Summaries(built);

        cards.Should().HaveCount(2);
        var rule = cards.Should().ContainSingle(card => card.Contains("Nexus mod wins over Plain archive")).Subject;
        rule.Should().Contain("collection Shared pack").And.Contain("Load Order page");
        rule.Should().Contain("sharing the collection leaves this rule out").And.Contain("one of its mods cannot be named");
    }

    [Fact]
    public async Task ARuleToAChildThatCannotBeNamedIsAWarning()
    {
        var built = await Build();
        await AddRule(built, built.Nexus, built.Plain);

        (await RuleCard(built, "Nexus mod wins over Plain archive")).Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ARuleToAGroupOutsideTheCollectionIsASuggestion()
    {
        var built = await Build();
        await AddRule(built, built.Nexus, built.Outside);

        // A personal rule between a shared mod and one outside is normal, and no manifest could carry it.
        (await RuleCard(built, "Nexus mod wins over Outside mod")).Severity.Should().Be(DiagnosticSeverity.Suggestion);
    }

    [Fact]
    public async Task ARuleToAGroupOutsideTheCollectionGetsACard()
    {
        var built = await Build();
        await AddRule(built, built.Nexus, built.Outside);

        var cards = await Summaries(built);

        var rule = cards.Should().ContainSingle(card => card.Contains("Nexus mod wins over Outside mod")).Subject;
        rule.Should().Contain("sharing the collection leaves this rule out").And.Contain("it names a mod outside the collection");
    }

    [Fact]
    public async Task ARuleWithBothReasonsGetsOneCard()
    {
        var built = await Build();
        await AddRule(built, built.Plain, built.Outside);

        var cards = await Summaries(built);

        cards.Where(card => card.Contains("wins over")).Should().ContainSingle();
    }

    [Fact]
    public async Task ARuleBetweenTwoShareableChildrenRaisesNothing()
    {
        var built = await Build();
        using (var tx = Connection.BeginTransaction())
        {
            var second = ShareableMods.AddLinkedGroup(tx, built.Loadout.LoadoutId, built.Collection, "Second nexus",
                ShareableMods.AddNexusItem(tx, "Second nexus", Cyberpunk2077Game.NexusModsGameId.Value, Domain, 6, 60));
            var result = await tx.Commit();
            await AddRule(built, built.Nexus, LoadoutItemGroupId.From(result[second.Value].Value));
        }

        var cards = await Summaries(built);

        cards.Should().ContainSingle().Which.Should().Contain("Plain archive");
    }

    [Fact]
    public async Task ARuleOutsideEveryCollectionRaisesNothing()
    {
        var built = await Build();
        using (var tx = Connection.BeginTransaction())
        {
            var other = AddEmptyGroup(tx, built.Loadout.LoadoutId, "Another outside");
            var result = await tx.Commit();
            await AddRule(built, built.Outside, LoadoutItemGroupId.From(result[other.Value].Value));
        }

        (await Summaries(built)).Should().ContainSingle().Which.Should().Contain("Plain archive");
    }

    [Fact]
    public void TheEmitterIsRegisteredForEveryGame()
    {
        ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<UnshareableModEmitter>().Should().ContainSingle();
    }
}
