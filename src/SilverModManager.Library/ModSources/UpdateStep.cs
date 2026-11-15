using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// One unit of a <see cref="ModUpdateBatch"/>: what a failure retries and names.
/// </summary>
/// <param name="update">Narrowed to this step, so applying it again redoes exactly this step.</param>
/// <param name="name">What the failure is shown under.</param>
internal abstract class UpdateStep(ModUpdate update, string name)
{
    public ModUpdate Update { get; } = update;

    public string Name { get; } = name;

    public abstract Task Apply(ModUpdateBatch batch);
}

/// <summary>
/// The shape most sources share: the items being updated, one download, then each old item replaced by it.
/// </summary>
internal abstract class DownloadThenReplaceStep(ModUpdate update, string name) : UpdateStep(update, name)
{
    /// <summary>
    /// The items the update replaces that are still in the library. Empty means the mod left the library since the
    /// update check.
    /// </summary>
    protected abstract IReadOnlyList<LibraryItem.ReadOnly> LoadCurrent();

    /// <summary>
    /// Null when nothing was downloaded (mod.io handed the user to the browser); the step then records nothing.
    /// </summary>
    protected abstract Task<LibraryItem.ReadOnly?> Download(IReadOnlyList<LibraryItem.ReadOnly> current, ModUpdateBatch batch);

    public sealed override async Task Apply(ModUpdateBatch batch)
    {
        var current = LoadCurrent();
        if (current.Count == 0) throw new InvalidOperationException($"{Name} {Update.CurrentVersion} is no longer in the library.");

        if (await Download(current, batch) is not { } added) return;

        batch.Downloaded(added);
        if (!batch.Request.Replace) return;

        foreach (var old in current) await batch.Replace(old, added);
    }

    /// <summary>
    /// The update's current items that still exist.
    /// </summary>
    protected static LibraryItem.ReadOnly[] LoadItems(IConnection connection, ModUpdate update)
        => [.. update.CurrentItems.Select(id => LibraryItem.Load(connection.Db, id)).Where(static item => item.IsValid())];
}
