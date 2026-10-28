using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Installers;

/// <summary>
/// Keeps Witcher Script Merger out of the loadout. A collection lists it as a required mod, and refusing it there
/// would leave the whole collection disabled, so inside a collection it installs as an empty group that counts as
/// installed; <see cref="Diagnostics.SkippedScriptMergerEmitter"/> explains the gap. Added by hand it is refused.
/// </summary>
public sealed class ScriptMergerArchiveInstaller(IServiceProvider provider)
    : ALibraryArchiveInstaller(provider, provider.GetRequiredService<ILogger<ScriptMergerArchiveInstaller>>())
{
    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        if (!ScriptMergerArchive.IsScriptMerger(libraryArchive))
            return ValueTask.FromResult<InstallerResult>(new NotSupported("not Script Merger"));

        var name = libraryArchive.AsLibraryFile().FileName.ToString();
        if (!IsInsideCollection(loadoutGroup, transaction, loadout))
            throw new ScriptMergerArchiveRefusedException(name);

        Logger.LogInformation("Skipping Witcher Script Merger archive `{Name}` from a collection: the app merges scripts itself", name);
        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    // The install job sets the parent when it creates the group, so a collection's installs are the ones whose parent is its group.
    private static bool IsInsideCollection(LoadoutItemGroup.New loadoutGroup, ITransaction transaction, Loadout.ReadOnly loadout)
    {
        var parent = loadoutGroup.GetLoadoutItem(transaction).ParentId;
        return parent.Value != 0 && NexusCollectionLoadoutGroup.Load(loadout.Db, parent).IsValid();
    }
}
