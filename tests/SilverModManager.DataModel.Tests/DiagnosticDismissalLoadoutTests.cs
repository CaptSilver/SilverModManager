using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// The Health Check dismissals a loadout keeps: tracked with the loadout, copied with it and removed with it.
/// </summary>
public class DiagnosticDismissalLoadoutTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<DiagnosticDismissalLoadoutTests>(helper)
{
    [Fact]
    public async Task TrackedEntitiesIncludeTheDismissalsOfTheirOwnLoadoutOnly()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        var dismissal = await AddDismissal(loadout.LoadoutId);
        var otherDismissal = await AddDismissal(other.LoadoutId);

        var owned = TrackedIds(loadout.Id);

        owned.Should().Contain(dismissal);
        owned.Should().NotContain(otherDismissal);
    }

    [Fact]
    public async Task CopyingALoadoutCopiesItsDismissals()
    {
        var loadout = await CreateLoadout();
        await AddDismissal(loadout.LoadoutId);

        var copy = await LoadoutManager.CopyLoadout(loadout.LoadoutId);

        var copied = DiagnosticDismissal.FindByLoadout(Connection.Db, copy.LoadoutId).Should().ContainSingle().Subject;
        copied.Subject.Should().Be("Game|a.dll");
        copied.Identity.Value.Should().Be("XxHash3:0011");
        copied.Title.Should().Be("a title");
        copied.Number.Should().Be(11);
        DiagnosticDismissal.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle("the original keeps its own row");
    }

    [Fact]
    public async Task DeletingALoadoutRemovesItsDismissals()
    {
        var loadout = await CreateLoadout();
        var dismissal = await AddDismissal(loadout.LoadoutId);

        await LoadoutManager.DeleteLoadout(loadout.LoadoutId, deactivateIfActive: false);

        DiagnosticDismissal.Load(Connection.Db, dismissal).IsValid().Should().BeFalse();
    }

    private HashSet<EntityId> TrackedIds(EntityId loadout) => Connection
        .Query<EntityId>($"SELECT Id FROM loadouts.TrackedEntitiesForLoadout({Connection.Db}, {loadout})")
        .ToHashSet();

    private async Task<DiagnosticDismissalId> AddDismissal(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var row = new DiagnosticDismissal.New(tx)
        {
            LoadoutId = loadoutId,
            Source = "SilverModManager.Games.Generic",
            Number = 11,
            Subject = "Game|a.dll",
            Identity = "XxHash3:0011",
            Title = "a title",
        };
        var result = await tx.Commit();
        return result.Remap(row).DiagnosticDismissalId;
    }
}
