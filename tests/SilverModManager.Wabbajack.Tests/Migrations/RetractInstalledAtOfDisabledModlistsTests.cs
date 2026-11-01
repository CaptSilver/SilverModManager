using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Migrations;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;

namespace SilverModManager.Wabbajack.Tests.Migrations;

/// <summary>
/// A list that was resumed and interrupted holds InstalledAt, no skip rows and a disabled group. Older versions read
/// that as incomplete because of the disabled flag; the migration keeps it reading that way under the rule that
/// ignores the flag.
/// </summary>
public class RetractInstalledAtOfDisabledModlistsTests(ITestOutputHelper helper) : InstallTestBase<RetractInstalledAtOfDisabledModlistsTests>(helper)
{
    private int _next = 0x5000;

    private async Task<EntityId> AddGroup(LoadoutId loadout, bool installed, bool enabled, bool skipped = false)
    {
        using var tx = Connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = EntityId.From((ulong)_next++),
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = "List", LoadoutId = loadout, IsDisabled = !enabled },
                },
            },
        };
        if (installed) tx.Add(id, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        if (skipped)
        {
            _ = new WabbajackSkippedFile.New(tx, out var skipId)
            {
                GroupId = WabbajackModlistLoadoutGroupId.From(id), To = "x/y.esp", Reason = "missing", Kind = SkipKind.MissingArchive,
                LoadoutItem = new LoadoutItem.New(tx, skipId) { Name = "skipped", LoadoutId = loadout },
            };
        }
        return (await tx.Commit()).Remap(group).Id;
    }

    private async Task Run()
    {
        var migration = new _0007_RetractInstalledAtOfDisabledModlists(NullLogger<_0007_RetractInstalledAtOfDisabledModlists>.Instance);
        await migration.Prepare(Connection.Db);
        using var tx = Connection.BeginTransaction();
        migration.Migrate(tx, Connection.Db);
        await tx.Commit();
    }

    private bool HasInstalledAt(EntityId group) => WabbajackModlistLoadoutGroup.Load(Connection.Db, group).Contains(WabbajackModlistLoadoutGroup.InstalledAt);

    /// <summary>What the page and the card read before the rule stopped looking at the toggle.</summary>
    private bool OldRuleSaysInstalled(EntityId group)
    {
        var db = Connection.Db;
        var read = WabbajackModlistLoadoutGroup.Load(db, group);
        var enabled = !read.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled;
        return read.Contains(WabbajackModlistLoadoutGroup.InstalledAt) && enabled && !WabbajackSkippedFile.FindByGroup(db, read.WabbajackModlistLoadoutGroupId).Any();
    }

    private bool NewRuleSaysInstalled(EntityId group)
    {
        var db = Connection.Db;
        var read = WabbajackModlistLoadoutGroup.Load(db, group);
        return read.Contains(WabbajackModlistLoadoutGroup.InstalledAt) && !WabbajackSkippedFile.FindByGroup(db, read.WabbajackModlistLoadoutGroupId).Any();
    }

    [Fact]
    public async Task ADisabledGroupLosesItsInstalledAtAnEnabledOneKeepsItAndADisabledOneWithoutItIsUntouched()
    {
        var loadout = await CreateLoadout();
        var disabled = await AddGroup(loadout.LoadoutId, installed: true, enabled: false);
        var enabled = await AddGroup(loadout.LoadoutId, installed: true, enabled: true);
        var neverFinished = await AddGroup(loadout.LoadoutId, installed: false, enabled: false);

        await Run();

        HasInstalledAt(disabled).Should().BeFalse();
        HasInstalledAt(enabled).Should().BeTrue();
        HasInstalledAt(neverFinished).Should().BeFalse();
        WabbajackModlistLoadoutGroup.Load(Connection.Db, neverFinished).AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task ASecondRunChangesNothing()
    {
        var loadout = await CreateLoadout();
        var disabled = await AddGroup(loadout.LoadoutId, installed: true, enabled: false);
        var enabled = await AddGroup(loadout.LoadoutId, installed: true, enabled: true);
        await Run();
        var installedAt = WabbajackModlistLoadoutGroup.Load(Connection.Db, enabled).InstalledAt;

        await Run();

        HasInstalledAt(disabled).Should().BeFalse();
        WabbajackModlistLoadoutGroup.Load(Connection.Db, enabled).InstalledAt.Should().Be(installedAt);
    }

    [Fact]
    public async Task EveryGroupReadsAfterTheMigrationExactlyWhatItReadBefore()
    {
        var loadout = await CreateLoadout();
        var groups = new List<EntityId>();
        foreach (var installed in new[] { true, false })
        foreach (var enabled in new[] { true, false })
        foreach (var skipped in new[] { true, false })
            groups.Add(await AddGroup(loadout.LoadoutId, installed, enabled, skipped));
        var before = groups.Select(OldRuleSaysInstalled).ToArray();
        before.Should().Contain(true).And.Contain(false);

        await Run();

        groups.Select(NewRuleSaysInstalled).Should().Equal(before);
    }
}
