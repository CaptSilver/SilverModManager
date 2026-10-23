using System.Text.Json;
using SilverModManager.Games.FromSoft.Saves;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Launch;

/// <summary>
/// What the save gate knows about one save folder whose session has not been settled: the hash of
/// the vanilla save as the session began, where the copies of the folder's save files are kept, and whether
/// restoring them failed. An entry without <see cref="Tainted"/> is a session that is running or whose end was
/// never seen; a tainted one waits for the user.
/// </summary>
/// <param name="Folder">The save folder, one Steam account's folder inside the prefix.</param>
/// <param name="Hash">The xxHash3 of the vanilla save when the session began, as 16 hex digits; empty when there was no such file.</param>
/// <param name="CopyPath">The folder holding the copies of the vanilla save and its <c>.bak</c> from the start of the session.</param>
/// <param name="Tainted">True when the copies could not be put back and the vanilla save may hold what a mod wrote.</param>
/// <param name="Stamp">When the session began, <c>yyyyMMddTHHmmssfffZ</c>.</param>
public sealed record SaveLedgerEntry(string Folder, string Hash, string CopyPath, bool Tainted, string Stamp);

/// <summary>
/// A modded session changed a vanilla save file. Kept until someone resolves it, so a diagnostic can name both files.
/// </summary>
/// <param name="Save">The vanilla file that was changed.</param>
/// <param name="Backup">Where the changed bytes were moved to; absent when the session removed the file.</param>
/// <param name="Restored">Whether the file now holds what it held before the session.</param>
/// <param name="Stamp">When the session that changed the file began, as in <see cref="SaveLedgerEntry.Stamp"/>.</param>
public sealed record SaveWrittenRecord(string Save, string Backup, bool Restored, string Stamp);

/// <summary>
/// The save gate's state for one installation, a JSON file under the FromSoft data root:
/// <c>&lt;root&gt;/&lt;game&gt;/&lt;installation&gt;/saves/ledger.json</c>, one <see cref="SaveLedgerEntry"/> per save folder.
/// The saves page reads the same file, so the field names are fixed. Copies of a running session sit in
/// <c>session/&lt;stamp&gt;/</c> and the bytes a mod wrote in <c>backups/</c>.
/// </summary>
public sealed class SaveLedger(AbsolutePath root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _lock = new();

    public static SaveLedger For(FromSoftSettings settings, IFileSystem fileSystem, GameInstallation installation)
    {
        var game = SaveBackups.FromSoftGameOf(installation);
        return new SaveLedger(FromSoftPaths.InstallationRoot(game.Layout, settings, fileSystem, FromSoftPaths.InstallationFolderName(game.Layout, installation.LocatorResult)) / "saves");
    }

    public AbsolutePath File => root / "ledger.json";

    /// <summary>The copies each session keeps, one folder per session stamp.</summary>
    public AbsolutePath Session => root / "session";

    /// <summary>The files a modded session changed, kept as <c>&lt;save&gt;.&lt;stamp&gt;-modded-session</c>.</summary>
    public AbsolutePath Backups => root / "backups";

    private AbsolutePath WrittenFile => root / "written.json";

    public IReadOnlyList<SaveLedgerEntry> Entries => Read<SaveLedgerEntry>(File);

    /// <summary>The changes of modded sessions nobody has looked at yet.</summary>
    public IReadOnlyList<SaveWrittenRecord> Written => Read<SaveWrittenRecord>(WrittenFile);

    public bool AnyTainted => Entries.Any(e => e.Tainted);

    /// <summary>Replaces the entry of the entry's folder, or adds it.</summary>
    internal void Put(SaveLedgerEntry entry)
    {
        lock (_lock) Write(File, [..Entries.Where(e => e.Folder != entry.Folder), entry]);
    }

    /// <summary>Drops the folder's entry and the copies it points at, with the session folder once it is empty.</summary>
    internal void Remove(SaveLedgerEntry entry)
    {
        lock (_lock)
        {
            Write(File, Entries.Where(e => e.Folder != entry.Folder).ToArray());
            if (Directory.Exists(entry.CopyPath)) Directory.Delete(entry.CopyPath, recursive: true);
            var stampFolder = Session / entry.Stamp;
            if (stampFolder.DirectoryExists() && !stampFolder.EnumerateFiles().Any()) stampFolder.DeleteDirectory(recursive: true);
        }
    }

    internal void Add(SaveWrittenRecord record)
    {
        lock (_lock) Write(WrittenFile, [..Written, record]);
    }

    /// <summary>
    /// Settles tainted entries once the user has dealt with the save: the entry and its copies are dropped. With a
    /// folder only that folder's entry is settled. The written records stay until <see cref="Dismiss"/>.
    /// </summary>
    public void Resolve(string? folder = null)
    {
        foreach (var entry in Entries.Where(e => e.Tainted && (folder is null || e.Folder == folder)).ToArray()) Remove(entry);
    }

    /// <summary>Drops a written record, once the user has seen it.</summary>
    public void Dismiss(SaveWrittenRecord record)
    {
        lock (_lock) Write(WrittenFile, Written.Where(r => r != record).ToArray());
    }

    internal static async Task<string> HashOf(IFileSystem fileSystem, string file, CancellationToken cancellationToken = default)
        => (await fileSystem.FromUnsanitizedFullPath(file).XxHash3Async(token: cancellationToken)).Value.ToString("x16");

    private static IReadOnlyList<T> Read<T>(AbsolutePath file)
        => file.FileExists ? JsonSerializer.Deserialize<List<T>>(System.IO.File.ReadAllText(file.GetFullPath()), Json) ?? [] : [];

    // Written beside the target and renamed over it, so a reader never sees half a file.
    private static void Write<T>(AbsolutePath file, IReadOnlyList<T> items)
    {
        file.Parent.CreateDirectory();
        var temporary = file.GetFullPath() + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json));
        System.IO.File.Move(temporary, file.GetFullPath(), overwrite: true);
    }
}
