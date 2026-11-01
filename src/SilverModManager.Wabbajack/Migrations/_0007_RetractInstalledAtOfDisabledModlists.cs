using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.SchemaVersions;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Migrations;

/// <summary>
/// Takes <see cref="WabbajackModlistLoadoutGroup.InstalledAt"/> off every modlist group that is disabled.
/// </summary>
/// <remarks>
/// A resume deletes the skip rows before it runs, so a finished list whose resume was interrupted holds
/// <c>InstalledAt</c>, no skip rows and a disabled group. Until now that read as an unfinished install because the group
/// was disabled. The install state no longer looks at the enable toggle, so without this it would read as installed.
/// Every group this touches read as unfinished before, and still does afterwards. The cost is that a list the user
/// finished and then switched off is offered Resume once.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0007_RetractInstalledAtOfDisabledModlists(ILogger<_0007_RetractInstalledAtOfDisabledModlists> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0007_RetractInstalledAtOfDisabledModlists));

    private readonly List<(EntityId Group, DateTimeOffset InstalledAt)> _groups = [];

    public Task Prepare(IDb db)
    {
        _groups.Clear();
        foreach (var group in WabbajackModlistLoadoutGroup.All(db))
        {
            if (group.InstalledAt is not { HasValue: true } installedAt) continue;
            if (!group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().IsDisabled) continue;
            _groups.Add((group.Id, installedAt.Value));
        }
        return Task.CompletedTask;
    }

    public void Migrate(ITransaction tx, IDb db)
    {
        foreach (var (group, installedAt) in _groups)
            tx.Retract(group, WabbajackModlistLoadoutGroup.InstalledAt, installedAt);

        logger.LogDebug("Took the install time off {Count} disabled modlist groups", _groups.Count);
    }
}
