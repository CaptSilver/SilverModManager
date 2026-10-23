using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.SortOrder;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using OneOf;

namespace SilverModManager.Games.RedEngine.Tests;

public class RedModSortOrderManagerTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<Cyberpunk2077Game>(helper)
{
    [Fact]
    public void RedModVarietyIsStillListedAfterAnotherGamesManagerRegisters()
    {
        var cyberpunkManager = GameInstallation.GetGame().SortOrderManager;
        cyberpunkManager.GetSortOrderVarieties().Should().ContainSingle(v => v is RedModSortOrderVariety);

        // Every other game registers its (empty) set on a manager of its own; that must not touch Cyberpunk's.
        using var otherManager = ServiceProvider.GetRequiredService<SortOrderManager>();
        otherManager.RegisterSortOrderVarieties([], new NoNexusIdStubbedGame(ServiceProvider));

        cyberpunkManager.GetSortOrderVarieties().Should().ContainSingle(v => v is RedModSortOrderVariety);
    }

    [Fact]
    public async Task MovesAreIgnoredWhenTheVarietyVetoesTheProposedOrder()
    {
        var (variety, sortOrderId, items) = await CreateSortOrderWithRedMods<VetoingVariety>();
        var before = variety.GetSortOrderItems(sortOrderId, Connection.Db).Select(i => i.DisplayName).ToArray();

        variety.Veto = true;
        await variety.MoveItemDelta(sortOrderId, items[0].Key, 2);
        await variety.MoveItems(sortOrderId, [items[0].Key], items[^1].Key, TargetRelativePosition.AfterTarget);

        variety.GetSortOrderItems(sortOrderId, Connection.Db).Select(i => i.DisplayName).Should().Equal(before);
        variety.Proposed.Should().HaveCount(2, "both moves were offered to the veto");

        variety.Veto = false;
        await variety.MoveItemDelta(sortOrderId, items[0].Key, 2);
        variety.GetSortOrderItems(sortOrderId, Connection.Db).Select(i => i.DisplayName).Should().NotEqual(before);
    }

    private async Task<(T Variety, SortOrderId SortOrderId, IReadOnlyList<RedModReactiveSortItem> Items)> CreateSortOrderWithRedMods<T>()
        where T : RedModSortOrderVariety
    {
        var loadout = await CreateLoadout();
        _ = GameInstallation.GetGame().SortOrderManager;
        await AddRedMods(loadout);
        await GameInstallation.GetGame().SortOrderManager.UpdateLoadOrders(loadout.LoadoutId);

        var variety = ActivatorUtilities.CreateInstance<T>(ServiceProvider);
        var id = variety.GetSortOrderIdFor(OneOf<LoadoutId, CollectionGroupId>.FromT0(loadout.LoadoutId), Connection.Db);
        id.HasValue.Should().BeTrue();
        return (variety, id.Value, variety.GetSortOrderItems(id.Value, Connection.Db));
    }

    private async Task AddRedMods(Loadout.ReadOnly loadout)
    {
        await using var tempDir = TemporaryFileManager.CreateFolder();
        foreach (var file in new[] { "one_mod.7z", "several_red_mods.7z" })
        {
            var sourcePath = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("LibraryArchiveInstallerTests/Resources/" + file);
            var copyPath = tempDir.Path.Combine(file);
            File.Copy(sourcePath.ToString(), copyPath.ToString(), overwrite: true);

            var libraryArchive = await RegisterLocalArchive(copyPath);
            await LoadoutManager.InstallItem(libraryArchive.AsLibraryFile().AsLibraryItem(), loadout);
        }
    }

    private sealed class VetoingVariety(IServiceProvider provider) : RedModSortOrderVariety(provider)
    {
        public bool Veto { get; set; }
        public List<int> Proposed { get; } = [];

        protected override bool CanApplyOrder(IReadOnlyList<SortItemData<SortItemKey<string>>> proposed)
        {
            Proposed.Add(proposed.Count);
            return !Veto;
        }
    }
}
