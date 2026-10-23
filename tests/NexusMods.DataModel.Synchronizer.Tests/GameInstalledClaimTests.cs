using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// "Treat as my file" moves a game-installed file into External Changes at its disk hash, and the undo hands
/// it back to the game.
/// </summary>
[Collection(GameInstalledStubbedGame.CollectionName)]
public class GameInstalledClaimTests : AIsolatedGameTest<GameInstalledClaimTests, GameInstalledStubbedGame>
{
    private const string Source = "StubSource";
    private static readonly GamePath CreationA = new(LocationId.Game, "Data/alpha.esm");
    private static readonly GamePath CreationB = new(LocationId.Game, "Data/beta.esm");

    public GameInstalledClaimTests(ITestOutputHelper helper) : base(helper) => GameInstalledStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<GameInstalledStubbedGame>()
            .AddUniversalGameLocator<GameInstalledStubbedGame>(Version.Parse("0.0.0"));
    }

    private IGameInstalledContentService Service => ServiceProvider.GetRequiredService<IGameInstalledContentService>();

    private static FakeGameInstalledRecognizer Recognizer => GameInstalledStubbedGame.Recognizer;

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteOnDisk(GamePath path, string text)
    {
        var file = OnDisk(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(text);
    }

    private static Hash HashOf(string text) => Encoding.UTF8.GetBytes(text).xxHash3();

    private static void Recognise(string sourceId, params GamePath[] files)
    {
        Recognizer.Items.RemoveAll(item => item.SourceId == sourceId);
        Recognizer.Items.Add(new GameInstalledContent(Source, sourceId, sourceId, Version: null, GameInstalledConfidence.GameRecord, CanBeClaimed: true, files));
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    private async Task<Dictionary<GamePath, SyncNode>> TreeOf(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        return tree;
    }

    private GameInstalledItem.ReadOnly[] Items(Loadout.ReadOnly loadout) =>
        GameInstalledGroup.FindByInstalledFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemGroup().OfTypeGameInstalledItem())
            .ToArray();

    private LoadoutItemWithTargetPath.ReadOnly[] OverridesRows(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(group => group.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .ToArray();

    private async Task<(Loadout.ReadOnly Loadout, GameInstalledItem.ReadOnly Item)> LoadoutWithOneItem(params (GamePath Path, string Text)[] files)
    {
        var loadout = await CreateLoadout();
        foreach (var (path, text) in files) await WriteOnDisk(path, text);
        Recognise("alpha", files.Select(f => f.Path).ToArray());
        await Apply(loadout);
        return (Refresh(loadout), Items(Refresh(loadout)).Single());
    }

    [Fact]
    public async Task ClaimingMovesTheFileToTheOverridesAtItsDiskHash()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));

        var outcome = await Service.Claim(item.GameInstalledItemId);

        outcome.MovedToExternalChanges.Should().BeEquivalentTo([CreationA]);
        var row = OverridesRows(Refresh(loadout)).Should().ContainSingle().Subject;
        ((GamePath)row.TargetPath).Should().Be(CreationA);
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be(HashOf("alpha 1"));
        (await FileStore.HaveFile(HashOf("alpha 1"))).Should().BeTrue("the overrides row needs an archive like any ingested file");
        Items(Refresh(loadout)).Should().BeEmpty("an item with no file left is deleted");
        GameInstalledPaths.For(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Should().ContainSingle()
            .Which.Path.Should().Be(CreationA.ToGamePathParentTuple(loadout.Id));
    }

    [Fact]
    public async Task ClaimingMovesEveryFileOfTheItem()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"), (CreationB, "beta 1"));

        var outcome = await Service.Claim(item.GameInstalledItemId);

        outcome.MovedToExternalChanges.Should().BeEquivalentTo([CreationA, CreationB]);
        OverridesRows(Refresh(loadout)).Select(row => (GamePath)row.TargetPath).Should().BeEquivalentTo([CreationA, CreationB]);
        Items(Refresh(loadout)).Should().BeEmpty();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task ClaimingAnItemThatCannotBeClaimedThrowsAndChangesNothing()
    {
        var loadout = await CreateLoadout();
        await WriteOnDisk(CreationA, "alpha 1");
        Recognizer.Items.Add(new GameInstalledContent(Source, "alpha", "alpha", Version: null, GameInstalledConfidence.GameRecord, CanBeClaimed: false, [CreationA]));
        await Apply(loadout);
        var item = Items(Refresh(loadout)).Single();

        var claim = () => Service.Claim(item.GameInstalledItemId);

        await claim.Should().ThrowAsync<InvalidOperationException>();
        Items(Refresh(loadout)).Should().ContainSingle();
        OverridesRows(Refresh(loadout)).Should().BeEmpty();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task TheNextSyncAfterAClaimNeitherDeletesNorBacksUpTheFile()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(item.GameInstalledItemId);

        var tree = await TreeOf(loadout);

        tree[CreationA].Actions.Should().NotHaveFlag(Actions.DeleteFromDisk);
        tree[CreationA].Actions.Should().NotHaveFlag(Actions.BackupFile);
        await Apply(loadout);
        OnDisk(CreationA).FileExists.Should().BeTrue();
        (await OnDisk(CreationA).ReadAllTextAsync()).Should().Be("alpha 1");
    }

    [Fact]
    public async Task ClaimingSkipsTheBackupWhenTheGameIgnoresBackupsForThePath()
    {
        GameInstalledStubbedGame.IgnoredBackups.Add(CreationA);
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));

        await Service.Claim(item.GameInstalledItemId);

        (await FileStore.HaveFile(HashOf("alpha 1"))).Should().BeFalse();
        OverridesRows(Refresh(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task AClaimedFileCanBeMovedIntoAModAndExtracts()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(item.GameInstalledItemId);

        using (var tx = Connection.BeginTransaction())
        {
            var mod = AddEmptyGroup(tx, loadout.LoadoutId, "My mod");
            tx.Add(OverridesRows(Refresh(loadout)).Single().Id, LoadoutItem.Parent, mod.Value);
            await tx.Commit();
        }

        var moved = LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, CreationA.ToGamePathParentTuple(loadout.Id)).Single();
        moved.AsLoadoutItem().Parent.AsLoadoutItem().Name.Should().Be("My mod");
        moved.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        var extracted = await FileStore.ExtractFiles([file.Hash]);
        Encoding.UTF8.GetString(extracted[file.Hash]).Should().Be("alpha 1");
        (await TreeOf(loadout))[CreationA].Actions.Should().NotHaveFlag(Actions.DeleteFromDisk);
    }

    [Fact]
    public async Task AClaimedPathStaysUnrecognisedOnTheNextReconcile()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(item.GameInstalledItemId);

        await Apply(loadout);
        await Apply(loadout);

        Items(Refresh(loadout)).Should().BeEmpty();
        GameInstalledPaths.For(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        OverridesRows(Refresh(loadout)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnclaimingGivesTheFileBackToTheGame()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(item.GameInstalledItemId);
        var claim = GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Single();

        var outcome = await Service.Unclaim(claim.GameInstalledClaimId);

        outcome.Restored.Should().BeTrue();
        outcome.Reason.Should().BeNull();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Should().BeEmpty();
        OverridesRows(Refresh(loadout)).Should().BeEmpty();
        var restored = Items(Refresh(loadout)).Single();
        restored.Source.Should().Be(Source);
        restored.SourceId.Should().Be("alpha");
        restored.Confidence.Should().Be(nameof(GameInstalledConfidence.GameRecord));
        restored.CanBeClaimed.Should().BeTrue();
        GameInstalledPaths.For(Connection.Db, loadout.LoadoutId)[CreationA].Hash.Should().Be(HashOf("alpha 1"));
        (await TreeOf(loadout))[CreationA].Actions.Should().Be(Actions.DoNothing);
    }

    [Fact]
    public async Task UnclaimingEachFileOfAnItemRebuildsOneItem()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"), (CreationB, "beta 1"));
        await Service.Claim(item.GameInstalledItemId);

        foreach (var claim in GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).ToArray())
            (await Service.Unclaim(claim.GameInstalledClaimId)).Restored.Should().BeTrue();

        var restored = Items(Refresh(loadout)).Should().ContainSingle().Subject;
        restored.AsLoadoutItemGroup().Children.OfTypeGameInstalledFile().Select(f => (GamePath)f.Path)
            .Should().BeEquivalentTo([CreationA, CreationB]);
        OverridesRows(Refresh(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnclaimingOverAnEditedCopyLeavesTheCopyAndSaysSo()
    {
        var (loadout, item) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(item.GameInstalledItemId);
        await WriteOnDisk(CreationA, "alpha edited by the user");
        await Synchronizer.ReindexState(GameInstallation);
        var claim = GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Single();

        var outcome = await Service.Unclaim(claim.GameInstalledClaimId);

        outcome.Restored.Should().BeFalse();
        outcome.Reason.Should().NotBeNullOrWhiteSpace();
        var row = OverridesRows(Refresh(loadout)).Should().ContainSingle().Subject;
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be(HashOf("alpha 1"), "the overrides row is left as it was");
        Items(Refresh(loadout)).Should().BeEmpty();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadout.Id).Should().ContainSingle("a refused undo keeps the claim");
    }

    [Fact]
    public async Task DeletingALoadoutDeletesItsClaimsAndNoOtherLoadoutsClaims()
    {
        var (loadoutA, itemA) = await LoadoutWithOneItem((CreationA, "alpha 1"));
        await Service.Claim(itemA.GameInstalledItemId);

        var loadoutB = await CreateLoadout();
        await Apply(loadoutB);
        // The first loadout's claim made the path the user's, so give the second loadout a claim of its own.
        using (var tx = Connection.BeginTransaction())
        {
            _ = new GameInstalledClaim.New(tx)
            {
                LoadoutId = loadoutB.LoadoutId,
                Path = CreationA.ToGamePathParentTuple(loadoutB.Id),
                Source = Source,
                SourceId = "alpha",
                ClaimedAt = DateTimeOffset.UtcNow,
            };
            await tx.Commit();
        }
        GameInstalledClaim.FindByLoadout(Connection.Db, loadoutA.Id).Should().ContainSingle();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadoutB.Id).Should().ContainSingle();

        await LoadoutManager.DeleteLoadout(loadoutA.LoadoutId, deactivateIfActive: false);

        GameInstalledClaim.FindByLoadout(Connection.Db, loadoutA.Id).Should().BeEmpty();
        GameInstalledClaim.FindByLoadout(Connection.Db, loadoutB.Id).Should().ContainSingle();
    }
}
