using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.DataModel.Sorting;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

using Manager = SilverModManager.DataModel.LoadoutManager;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Installing, removing and replacing groups run the rule resolver, so stored rules hold afterwards and every
/// group ends up with exactly one priority row.
/// </summary>
public class GroupRuleHooksTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GroupRuleHooksTests>(helper)
{
    [Fact]
    public async Task AnInstallWithNoRulesLandsAtTheEnd()
    {
        var loadout = await CreateLoadout();
        await AddGroups(loadout.LoadoutId, "A", "B");

        await LoadoutManager.InstallItemWrapper(loadout.LoadoutId, tx => Task.FromResult(AddEmptyGroup(tx, loadout.LoadoutId, "New")));

        NamesByPriority(loadout.LoadoutId).Should().Equal("A", "B", "New");
        PriorityRows(loadout.LoadoutId).Select(r => r.Target).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task AnInstallRestoresTheOrderTheStoredRulesDemand()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B");
        // A has to win over B, but the stored priorities have it the other way round.
        await AddRule(loadout.LoadoutId, g["A"], g["B"], GroupRuleKind.After);

        await LoadoutManager.InstallItemWrapper(loadout.LoadoutId, tx => Task.FromResult(AddEmptyGroup(tx, loadout.LoadoutId, "New")));

        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "A", "New");
        PriorityRows(loadout.LoadoutId).Select(r => r.Target).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task RemovingAGroupRetractsItsRulesAndKeepsTheOrderOfTheRest()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "B", "A", "C");
        var keptRule = await AddRule(loadout.LoadoutId, g["C"], g["B"], GroupRuleKind.After);
        await AddRule(loadout.LoadoutId, g["A"], g["B"], GroupRuleKind.After);

        await LoadoutManager.RemoveItems([g["A"]]);

        NamesByPriority(loadout.LoadoutId).Should().Equal("B", "C");
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL);
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Select(r => r.Id).Should().Equal(keptRule);
    }

    [Fact]
    public async Task ReplacingGroupsWritesOneRowPerGroupAndHoldsTheRules()
    {
        var loadout = await CreateLoadout();
        var g = await AddGroups(loadout.LoadoutId, "A", "B", "Old");
        await AddRule(loadout.LoadoutId, g["A"], g["B"], GroupRuleKind.After);
        await AddRule(loadout.LoadoutId, g["Old"], g["A"], GroupRuleKind.Before);
        var archive = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("nested_archive.zip");
        var libraryItem = (await ServiceProvider.GetRequiredService<ILibraryService>().AddLocalFile(archive)).AsLibraryFile().AsLibraryItem();

        await LoadoutManager.ReplaceItems(loadout.LoadoutId, [g["Old"]], libraryItem);

        var names = NamesByPriority(loadout.LoadoutId);
        names.Should().HaveCount(3);
        names.Take(2).Should().Equal("B", "A");
        PriorityRows(loadout.LoadoutId).Select(r => r.Target).Should().OnlyHaveUniqueItems();
        PriorityValues(loadout.LoadoutId).Should().Equal(1UL, 2UL, 3UL);
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle();
    }

    [Fact]
    public async Task ATransactionHoldingTheGroupFactoryAndTheResolverForOneLoadoutFails()
    {
        var loadout = await CreateLoadout();
        await AddGroups(loadout.LoadoutId, "A");

        using var tx = Connection.BeginTransaction();
        Manager.AddEmptyGroup(tx, loadout.LoadoutId, "Factory");
        tx.Add(new Manager.ApplyGroupRulesTxFunc(loadout.LoadoutId, RuleDelta.Empty));

        var act = async () => await tx.Commit();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*priority*");
        NamesByPriority(loadout.LoadoutId).Should().Equal("A");
    }

    private async Task<LoadoutGroupRuleId> AddRule(LoadoutId loadoutId, LoadoutItemGroupId source, LoadoutItemGroupId other, GroupRuleKind kind)
    {
        using var tx = Connection.BeginTransaction();
        var rule = new LoadoutGroupRule.New(tx) { LoadoutId = loadoutId, SourceId = source, OtherId = other, Kind = kind };
        var result = await tx.Commit();
        return LoadoutGroupRuleId.From(result[rule.Id].Value);
    }

    private async Task<Dictionary<string, LoadoutItemGroupId>> AddGroups(LoadoutId loadoutId, params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select((name, i) =>
        {
            var group = AddEmptyGroup(tx, loadoutId, name);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)(i + 1) * 10) };
            return (name, group);
        }).ToArray();
        var result = await tx.Commit();
        return added.ToDictionary(a => a.name, a => LoadoutItemGroupId.From(result[a.group].Value));
    }

    private string[] NamesByPriority(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => p.Target.AsLoadoutItem().Name)
        .ToArray();

    private ulong[] PriorityValues(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Priority)
        .Select(p => (ulong)p.Priority.Value)
        .ToArray();

    private (ulong Row, ulong Target, ulong Priority)[] PriorityRows(LoadoutId loadoutId) => LoadoutItemGroupPriority
        .FindByLoadout(Connection.Db, loadoutId)
        .OrderBy(p => p.Id)
        .Select(p => ((ulong)p.Id.Value, (ulong)p.TargetId.Value, (ulong)p.Priority.Value))
        .ToArray();
}
