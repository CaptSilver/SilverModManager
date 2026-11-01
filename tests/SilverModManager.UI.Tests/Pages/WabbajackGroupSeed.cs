using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Writes the rows an install of a modlist leaves behind, one step at a time, so a test can stop at any of them.</summary>
internal static class WabbajackGroupSeed
{
    /// <summary>The group the install job creates: disabled, with no <see cref="WabbajackModlistLoadoutGroup.InstalledAt"/>.</summary>
    public static async Task<EntityId> AddGroup(IConnection connection, EntityId list, LoadoutId loadout, string name)
    {
        using var tx = connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = list,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout, IsDisabled = true },
                },
            },
        };
        return (await tx.Commit()).Remap(group).Id;
    }

    public static async Task Finish(IConnection connection, EntityId group)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(group, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        await tx.Commit();
    }

    /// <summary>The user's toggle in the loadout's left menu.</summary>
    public static async Task SetEnabled(IConnection connection, EntityId group, bool enabled)
    {
        using var tx = connection.BeginTransaction();
        if (enabled) tx.Retract(group, LoadoutItem.Disabled, Null.Instance);
        else tx.Add(group, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    public static async Task<EntityId> AddSkip(IConnection connection, EntityId group, LoadoutId loadout, string to = "x/y.esp")
    {
        using var tx = connection.BeginTransaction();
        var skipped = new WabbajackSkippedFile.New(tx, out var id)
        {
            GroupId = WabbajackModlistLoadoutGroupId.From(group), To = to, Reason = "missing", Kind = SkipKind.MissingArchive,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "skipped", LoadoutId = loadout },
        };
        return (await tx.Commit()).Remap(skipped).Id;
    }

    public static async Task Delete(IConnection connection, EntityId entity)
    {
        using var tx = connection.BeginTransaction();
        tx.Delete(entity, recursive: true);
        await tx.Commit();
    }
}
