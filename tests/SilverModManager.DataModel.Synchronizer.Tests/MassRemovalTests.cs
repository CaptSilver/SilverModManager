using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// An apply that would take most of what a loadout deployed out of the game folder asks first, names why, and leaves
/// the files alone unless the answer is yes. Switching a collection off and pressing Apply is how a whole mod list
/// left a game folder with nothing in the log or on screen to say so.
/// </summary>
public class MassRemovalTests(ITestOutputHelper helper) : AStubbedGameTest<MassRemovalTests>(helper)
{
    private readonly SwitchableMassRemovalPrompt _prompt = new();

    // Three files and half of what is deployed, or four files whatever the share, so the numbers stay small.
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services)
            .AddSingleton(new MassRemovalThreshold(MinimumFiles: 3, Files: 40, Share: 0.5))
            .AddSingleton<IMassRemovalPrompt>(_prompt);

    private AbsolutePath GameDir => GameInstallation.Locations[LocationId.Game].Path;

    private static RelativePath[] Files(string folder, int count) => Enumerable.Range(0, count).Select(i => (RelativePath)$"{folder}/f{i}.txt").ToArray();

    private async Task AddMod(Loadout.ReadOnly loadout, string name, int files, LoadoutItemGroupId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, Files("mods/" + name.Replace(' ', '_'), files), loadout.LoadoutId, name, parentGroup: parent);
        await tx.Commit();
    }

    private async Task SwitchOff(EntityId item)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(item, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private async Task<(Loadout.ReadOnly Loadout, CollectionGroup.ReadOnly Collection)> AppliedCollection(int files)
    {
        var loadout = await CreateLoadout();
        var collection = await CreateCollection(loadout.LoadoutId, "Big Collection");
        await AddMod(loadout, "Part One", files / 2, LoadoutItemGroupId.From(collection.Id));
        await AddMod(loadout, "Part Two", files - files / 2, LoadoutItemGroupId.From(collection.Id));
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        GameDir.EnumerateFiles(recursive: true).Count(f => f.ToString().Contains("/mods/")).Should().Be(files, "the collection is deployed before it is switched off");
        return (Refresh(loadout), collection);
    }

    private int DeployedModFiles => GameDir.EnumerateFiles(recursive: true).Count(f => f.ToString().Contains("/mods/"));

    [Fact]
    public async Task SwitchingACollectionOffThenApplying_AsksNamingTheCollectionAndItsFiles()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = false;

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        (await act.Should().ThrowAsync<MassRemovalException>()).Which.Declined.Should().BeTrue();
        var asked = _prompt.Asked.Should().ContainSingle().Subject;
        asked.FileCount.Should().Be(8);
        asked.Reasons.Should().ContainSingle().Which.Should().Be(new MassRemovalReason("Big Collection", 8));
        asked.Opening("Stubbed Game").Should().Contain("Big Collection is switched off, so applying removes its 8 files");
    }

    [Fact]
    public async Task Cancel_LeavesEveryFileOnDisk()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = false;

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);
        await act.Should().ThrowAsync<MassRemovalException>();

        DeployedModFiles.Should().Be(8);
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree.Values.Count(n => n.Actions.HasFlag(SilverModManager.Abstractions.Loadouts.Synchronizers.Rules.Actions.DeleteFromDisk)).Should().Be(8, "nothing was recorded as applied, so the removal is still pending");
    }

    [Fact]
    public async Task Confirming_RemovesTheFiles()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = true;

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        _prompt.Asked.Should().ContainSingle();
        DeployedModFiles.Should().Be(0);
    }

    [Fact]
    public async Task ASmallRemoval_DoesNotAsk()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Keeper", 30);
        await AddMod(loadout, "Tiny", 2);
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var tiny = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Tiny");
        await SwitchOff(tiny.Id);

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        _prompt.Asked.Should().BeEmpty("two files are under the minimum, though they are a share of what is deployed");
        DeployedModFiles.Should().Be(30);
    }

    [Fact]
    public async Task AMidSizedRemovalFromABigDeployment_DoesNotAsk()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Keeper", 36);
        await AddMod(loadout, "Medium", 5);
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var medium = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Medium");
        await SwitchOff(medium.Id);

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        _prompt.Asked.Should().BeEmpty("five of 41 is neither half nor over the file count");
        DeployedModFiles.Should().Be(36);
    }

    [Fact]
    public async Task ManyFilesAsksWhateverTheShare()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Keeper", 100);
        await AddMod(loadout, "Large", 45);
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var large = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Large");
        await SwitchOff(large.Id);
        _prompt.Answer = true;

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        _prompt.Asked.Should().ContainSingle().Which.Reasons.Should().ContainSingle().Which.Should().Be(new MassRemovalReason("Large", 45));
    }

    [Fact]
    public async Task FilesOfAModThatWasRemovedAreCountedWithoutAName()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Gone", 8);
        await SynchronizerService.Synchronize(loadout.LoadoutId);
        var gone = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == "Gone");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(gone.Id, recursive: true);
            await tx.Commit();
        }
        _prompt.Answer = true;

        await SynchronizerService.Synchronize(loadout.LoadoutId);

        var asked = _prompt.Asked.Should().ContainSingle().Subject;
        asked.Reasons.Should().BeEmpty();
        asked.Unexplained.Should().Be(8);
        asked.Opening("Stubbed Game").Should().Contain("8 files");
    }

    [Fact]
    public async Task ConfirmingInAdvance_AppliesWithoutAsking()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = false;

        using (MassRemovalConsent.Confirmed()) await SynchronizerService.Synchronize(loadout.LoadoutId);

        _prompt.Asked.Should().BeEmpty();
        DeployedModFiles.Should().Be(0);
    }

    [Fact]
    public async Task ARefusingScope_StopsWithoutAskingAndNamesTheFlag()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = true;

        using var refused = MassRemovalConsent.Refused();
        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        var refusal = (await act.Should().ThrowAsync<MassRemovalException>()).Which;
        refusal.Declined.Should().BeFalse("nobody answered; the GUI has no reason to stay quiet");
        refusal.CommandLineHint.Should().Contain("--confirm-removal");
        _prompt.Asked.Should().BeEmpty("a command forwarded to the open window must not open a dialog");
        DeployedModFiles.Should().Be(8);
    }

    [Fact]
    public async Task APlayerWhoCannotBeAsked_GetsAReadableRefusalThatNamesTheFlag()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Inner = new RefusingMassRemovalPrompt();

        var act = async () => await SynchronizerService.Synchronize(loadout.LoadoutId);

        var refusal = (await act.Should().ThrowAsync<MassRemovalException>()).Which;
        refusal.Should().BeAssignableTo<UserFacingException>();
        refusal.Message.Should().Contain("Big Collection is switched off").And.Contain("nothing was changed").And.NotContain("`");
        refusal.CommandLineHint.Should().Contain("--confirm-removal");
        DeployedModFiles.Should().Be(8);
    }

    [Fact]
    public async Task SwitchingLoadouts_DoesNotAskAboutTheOldLoadoutsFiles()
    {
        var (loadout, collection) = await AppliedCollection(files: 8);
        await SwitchOff(collection.Id);
        _prompt.Answer = false;

        using (SyncOrigin.Begin(SyncTrigger.SwitchLoadout)) await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);

        _prompt.Asked.Should().BeEmpty("the reset that follows removes them anyway, and the player asked for it");
        DeployedModFiles.Should().Be(0);
    }

    [Fact]
    public void TheDefaultThresholdIsHalfOfWhatIsDeployedOrManyFiles()
    {
        MassRemovalThreshold.Default.Should().Be(new MassRemovalThreshold(MinimumFiles: 10, Files: 200, Share: 0.5));
    }
}

public sealed class SwitchableMassRemovalPrompt : IMassRemovalPrompt
{
    public IMassRemovalPrompt? Inner { get; set; }

    public bool Answer { get; set; }

    public List<MassRemoval> Asked { get; } = [];

    public ValueTask<bool> Confirm(GameInstallation installation, MassRemoval removal, CancellationToken cancellationToken)
    {
        Asked.Add(removal);
        return Inner is { } inner ? inner.Confirm(installation, removal, cancellationToken) : ValueTask.FromResult(Answer);
    }
}
