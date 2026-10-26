using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The per-loadout INI baseline rows and the two loadout flags that go with them: tracked with the loadout,
/// copied with it and removed with it, and defaulting on or off for a loadout written before they existed.
/// </summary>
public class LoadoutIniStateTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LoadoutIniStateTests>(helper)
{
    private static readonly GamePath Ini = new(LocationId.Preferences, "Skyrim.ini");
    private static readonly Hash Baseline = Hash.From(0x1111UL);
    private static readonly Hash Baked = Hash.From(0x2222UL);

    [Fact]
    public async Task ALoadoutWrittenWithoutTheFlagsOwnsItsInisAndKeepsNoSeparateSaves()
    {
        var loadout = await CreateLoadout();

        var read = Loadout.Load(Connection.Db, loadout.LoadoutId);

        read.Contains(Loadout.OwnsGameInis).Should().BeFalse("the fixture never wrote the attribute");
        Loadout.OwnsGameInis.Get(read).Should().BeTrue();
        Loadout.KeepSavesWithLoadout.Get(read).Should().BeFalse();
    }

    [Fact]
    public async Task TrackedEntitiesIncludeTheIniStateOfTheirOwnLoadoutOnly()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        var state = await AddState(loadout.LoadoutId);
        var otherState = await AddState(other.LoadoutId);

        var owned = TrackedIds(loadout.Id);

        owned.Should().Contain(state);
        owned.Should().NotContain(otherState);
    }

    [Fact]
    public async Task CopyingALoadoutCopiesItsIniStateAndFlagsOntoTheCopy()
    {
        var loadout = await CreateLoadout();
        await AddState(loadout.LoadoutId);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, true);
            tx.Add(loadout.Id, Loadout.OwnsGameInis, false);
            await tx.Commit();
        }

        var copy = await LoadoutManager.CopyLoadout(loadout.LoadoutId);

        var copied = LoadoutIniState.FindByLoadout(Connection.Db, copy.LoadoutId).Should().ContainSingle().Subject;
        copied.Path.Item1.Should().Be(copy.Id, "the path's loadout part points at the copy");
        (copied.Path.Item2, copied.Path.Item3).Should().Be((Ini.LocationId, Ini.Path));
        copied.Baseline.Should().Be(Baseline);
        copied.LastBakedHash.Value.Should().Be(Baked);
        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle("the original keeps its own row");
        Loadout.KeepSavesWithLoadout.Get(copy).Should().BeTrue();
        Loadout.OwnsGameInis.Get(copy).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingALoadoutRemovesItsIniState()
    {
        var loadout = await CreateLoadout();
        var state = await AddState(loadout.LoadoutId);

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId, deactivateIfActive: false);

        LoadoutIniState.Load(Connection.Db, state).IsValid().Should().BeFalse();
    }

    private HashSet<EntityId> TrackedIds(EntityId loadout) => Connection
        .Query<EntityId>($"SELECT Id FROM loadouts.TrackedEntitiesForLoadout({Connection.Db}, {loadout})")
        .ToHashSet();

    private async Task<LoadoutIniStateId> AddState(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var state = new LoadoutIniState.New(tx)
        {
            LoadoutId = loadoutId,
            Path = Ini.ToGamePathParentTuple(loadoutId),
            Baseline = Baseline,
            LastBakedHash = Baked,
        };
        var result = await tx.Commit();
        return result.Remap(state).LoadoutIniStateId;
    }
}
