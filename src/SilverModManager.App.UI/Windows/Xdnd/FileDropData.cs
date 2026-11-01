using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Dropped paths as a DataObject holding DataFormats.Files, which is what the window's drop handlers read.
/// IStorageItem can't be implemented outside Avalonia (it is sealed to client code in 11.3), so the items come
/// from a real <see cref="IStorageProvider"/>, i.e. the window's own. That makes creation async.
/// </summary>
public sealed class FileDropData(IStorageProvider storage) : IFileDropFactory
{
    /// <summary>Items in path order. A path that doesn't exist, or isn't absolute, is left out and listed in <see cref="FileDrop.Missing"/>.</summary>
    public async Task<FileDrop> CreateAsync(IReadOnlyList<string> paths)
    {
        var items = new List<IStorageItem>(paths.Count);
        var missing = new List<string>();

        foreach (var path in paths)
        {
            var item = await TryOpenAsync(path);
            if (item is null) missing.Add(path);
            else items.Add(item);
        }

        var data = new DataObject();
        data.Set(DataFormats.Files, items);
        return new FileDrop(data, missing);
    }

    private async Task<IStorageItem?> TryOpenAsync(string path)
    {
        if (Path.IsPathRooted(path) is false) return null;

        var uri = FileUri(path);
        if (Directory.Exists(path)) return await storage.TryGetFolderFromPathAsync(uri);
        return await storage.TryGetFileFromPathAsync(uri);
    }

    /// <summary>Segments are escaped one by one so '#', '%' and '?' stay in the name instead of starting a fragment, an escape or a query.</summary>
    private static Uri FileUri(string path) =>
        new("file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
}

/// <param name="Data">Carries DataFormats.Files; the session reuses it for the whole drag.</param>
/// <param name="Missing">Paths that could not be opened, in input order.</param>
public sealed record FileDrop(DataObject Data, IReadOnlyList<string> Missing);
