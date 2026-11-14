using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// Which entry in <c>shortcuts.vdf</c> is ours.
/// </summary>
/// <param name="RecordedAppId">The id recorded when the entry was added, tried first.</param>
/// <param name="QuotedExes">The <c>exe</c> values (quoted, as stored) that start this app, tried when no id matches.</param>
public sealed record ShortcutMatch(uint? RecordedAppId, IReadOnlySet<string> QuotedExes);

/// <summary>
/// Adds, replaces, removes and reads this app's entry in Steam's binary <c>shortcuts.vdf</c>. Every other entry
/// keeps its exact bytes, and the file goes through <see cref="SteamConfigFileEditor"/>.
/// </summary>
public sealed class ShortcutsVdfWriter(IBinaryKeyValues kv, SteamConfigFileEditor editor)
{
    private const string FileName = "shortcuts.vdf";
    private const string MapName = "shortcuts";
    internal const string UnparseableRefusal = "Steam's shortcuts.vdf could not be parsed.";
    internal const string MismatchRefusal = "Steam's shortcuts.vdf did not read back the entry that was written, so the original was restored.";
    internal const string SteamRunningRefusal = "Steam is running. It rewrites its shortcuts when it exits, so a change made now would be lost. " +
                                                "Close Steam completely (including the tray icon) and try again.";

    /// <summary>
    /// Stores <paramref name="entry"/> in place of the entry <paramref name="match"/> finds, or under the next free
    /// integer key when there is none. A missing file is created when its folder exists.
    /// </summary>
    public SteamConfigEdit AddOrReplace(AbsolutePath file, SteamShortcutEntry entry, ShortcutMatch match)
    {
        string? key = null;
        return editor.Edit(file,
            bytes =>
            {
                if (Open(bytes) is not (var root, var shortcuts)) return (null, UnparseableRefusal);

                var index = IndexOf(shortcuts, match);
                key = index >= 0 ? shortcuts.Children[index].Name : NextFreeKey(shortcuts);
                var node = entry.ToKv(key);
                if (index >= 0) shortcuts.Children[index] = node; else shortcuts.Children.Add(node);
                return (kv.Write(root), null);
            },
            readBack => key is not null
                        && Open(readBack) is (_, var shortcuts)
                        && shortcuts.Children.FirstOrDefault(c => c.Name == key) is { } stored
                        && BinaryKv.SameTree(stored, entry.ToKv(key)),
            createIfMissing: file.Parent.DirectoryExists(),
            missingFileRefusal: $"Steam's {file} does not exist and neither does its folder.",
            mismatchRefusal: MismatchRefusal,
            steamRunningRefusal: SteamRunningRefusal);
    }

    /// <summary>
    /// Drops the entry <paramref name="match"/> finds and nothing else.
    /// </summary>
    public SteamConfigEdit Remove(AbsolutePath file, ShortcutMatch match)
        => editor.Edit(file,
            bytes =>
            {
                if (Open(bytes) is not (var root, var shortcuts)) return (null, UnparseableRefusal);

                var index = IndexOf(shortcuts, match);
                if (index < 0) return (null, $"Steam's {file} has no entry for this app, so nothing was removed.");
                shortcuts.Children.RemoveAt(index);
                return (kv.Write(root), null);
            },
            readBack => Open(readBack) is (_, var shortcuts) && IndexOf(shortcuts, match) < 0,
            mismatchRefusal: "Steam's shortcuts.vdf still has the entry after it was removed, so the original was restored.",
            steamRunningRefusal: SteamRunningRefusal);

    /// <summary>
    /// Every entry in the file; none when the file is missing.
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be parsed.</exception>
    public IReadOnlyList<SteamShortcutEntry> Read(AbsolutePath file)
    {
        if (!file.FileExists) return [];
        if (Open(File.ReadAllBytes(file.ToString())) is not (_, var shortcuts)) throw new InvalidDataException(UnparseableRefusal);
        return shortcuts.Children.OfType<BinaryKvMap>().Select(SteamShortcutEntry.FromKv).ToList();
    }

    /// <summary>
    /// The entry that is ours: the one with the recorded id, else the first with one of the known executables.
    /// </summary>
    public static SteamShortcutEntry? Find(IReadOnlyList<SteamShortcutEntry> entries, ShortcutMatch match)
        => (match.RecordedAppId is { } id ? entries.FirstOrDefault(e => e.AppId == id) : null)
           ?? entries.FirstOrDefault(e => match.QuotedExes.Contains(e.Exe));

    /// <summary>
    /// Where an account's shortcuts live.
    /// </summary>
    public static string ShortcutsFilePath(AbsolutePath steamRoot, uint accountId)
        => steamRoot.Combine("userdata").Combine(accountId.ToString()).Combine("config").Combine(FileName).ToString();

    /// <summary>
    /// The parsed file and its <c>shortcuts</c> map (added when the file has none yet), or null when it cannot be parsed.
    /// </summary>
    private (BinaryKvMap Root, BinaryKvMap Shortcuts)? Open(byte[] bytes)
    {
        try
        {
            var root = bytes.Length == 0 ? new BinaryKvMap("", []) : kv.Read(bytes);
            var shortcuts = root.Children.OfType<BinaryKvMap>().FirstOrDefault(c => string.Equals(c.Name, MapName, StringComparison.OrdinalIgnoreCase));
            if (shortcuts is null)
            {
                // A file with no shortcuts map yet gets one; anything else at the top level is left in place.
                shortcuts = new BinaryKvMap(MapName, []);
                root.Children.Add(shortcuts);
            }
            return (root, shortcuts);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int IndexOf(BinaryKvMap shortcuts, ShortcutMatch match)
    {
        if (match.RecordedAppId is { } id)
        {
            var byId = shortcuts.Children.FindIndex(c => c is BinaryKvMap m && m.Find("appid") is BinaryKvInt32 n && unchecked((uint)n.Value) == id);
            if (byId >= 0) return byId;
        }
        return shortcuts.Children.FindIndex(c => c is BinaryKvMap m && m.Find("exe") is BinaryKvString exe && match.QuotedExes.Contains(exe.Value));
    }

    private static string NextFreeKey(BinaryKvMap shortcuts)
        => (shortcuts.Children.Select(c => int.TryParse(c.Name, out var n) ? n : -1).DefaultIfEmpty(-1).Max() + 1).ToString();
}
