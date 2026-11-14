using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// The outcome of one <see cref="SteamConfigFileEditor.Edit"/>.
/// </summary>
/// <param name="Refusal">Why nothing was changed (or why the change was taken back), or null on success.</param>
/// <param name="Backup">The copy of the file as it was just before the edit, or null when none was made.</param>
/// <param name="Original">The copy of the file as it was before this app first edited it, or null when none exists.</param>
/// <param name="SteamStartedDuringWrite">
/// Steam was not running when the edit began but is now, so it may rewrite the file when it exits and lose the change.
/// </param>
public sealed record SteamConfigEdit(string? Refusal, AbsolutePath? Backup, AbsolutePath? Original, bool SteamStartedDuringWrite)
{
    /// <summary>
    /// What to tell the user when <see cref="SteamStartedDuringWrite"/> is set, else null.
    /// </summary>
    public string? Note => SteamStartedDuringWrite ? SteamConfigFileEditor.SteamStartedNote : null;

    /// <summary>
    /// Whether the edit was refused.
    /// </summary>
    public bool IsRefused => Refusal is not null;
}

/// <summary>
/// The write sequence every edit of a Steam-owned config file goes through: refuse while Steam runs, keep the
/// user's original file once, keep the previous file per write, replace atomically, read back and restore on a
/// mismatch, then check that Steam did not start in the meantime. Each caller supplies only how to change the
/// bytes and how to tell the change was stored.
/// </summary>
/// <remarks>
/// <c>&lt;file&gt;.silvermod-orig</c> is written once and never replaced, so the user's file from before this app
/// touched it survives any number of edits. <c>&lt;file&gt;.silvermod-bak</c> is replaced on every edit.
/// </remarks>
public sealed class SteamConfigFileEditor(IProcessTracker tracker)
{
    public const string OriginalSuffix = ".silvermod-orig";
    public const string BackupSuffix = ".silvermod-bak";
    private const string TempSuffix = ".silvermod-tmp";

    public const string SteamRunningRefusal = "Steam rewrites its config on exit, so a launch option written while Steam is running " +
                                              "is lost. Close Steam completely (including the tray icon) and try again.";

    public const string SteamStartedNote = "Steam started while the file was being written, so the change may be lost. Close Steam and try again.";

    /// <summary>
    /// Changes <paramref name="file"/> through <paramref name="edit"/>.
    /// </summary>
    /// <param name="edit">
    /// Gets the file's bytes (none when it is missing and <paramref name="createIfMissing"/>) and returns the bytes
    /// to store, or a refusal. It runs before anything in Steam's folder changes, so it may do work that must be
    /// safe first.
    /// </param>
    /// <param name="verify">Gets the bytes read back after the write; false restores the previous file.</param>
    /// <param name="missingFileRefusal">The refusal for a missing file, when the caller has a better sentence.</param>
    /// <param name="mismatchRefusal">The refusal for a read-back that fails <paramref name="verify"/>, when the caller can say what was written.</param>
    /// <param name="steamRunningRefusal">The refusal while Steam runs, when the file holds something other than launch options.</param>
    public SteamConfigEdit Edit(AbsolutePath file, Func<byte[], (byte[]? Bytes, string? Refusal)> edit, Func<byte[], bool> verify, bool createIfMissing = false, string? missingFileRefusal = null, string? mismatchRefusal = null, string? steamRunningRefusal = null)
    {
        if (tracker.IsSteamRunning()) return Refuse(steamRunningRefusal ?? SteamRunningRefusal);

        var path = file.ToString();
        var exists = file.FileExists;
        if (!exists && !createIfMissing) return Refuse(missingFileRefusal ?? $"Steam's {path} does not exist, so there is nothing to edit.");

        byte[] before;
        try { before = exists ? File.ReadAllBytes(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refuse($"Steam's {path} could not be read ({ex.Message}), so it was left alone."); }

        var (bytes, refusal) = edit(before);
        if (refusal is not null) return Refuse(refusal);
        if (bytes is null) throw new InvalidOperationException("An edit must return bytes or a refusal.");

        AbsolutePath? original = null, backup = null;
        if (exists)
        {
            var originalPath = file.Parent.Combine(file.FileName + OriginalSuffix);
            var backupPath = file.Parent.Combine(file.FileName + BackupSuffix);
            try
            {
                if (!originalPath.FileExists) File.Copy(path, originalPath.ToString(), overwrite: false);
                original = originalPath;
                File.Copy(path, backupPath.ToString(), overwrite: true);
                backup = backupPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse($"The backup copy {(original is null ? originalPath : backupPath)} could not be written ({ex.Message}), so the file was left alone.");
            }
        }

        try { WriteAtomically(path, bytes, exists); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SteamConfigEdit($"Steam's {path} could not be written ({ex.Message}), so it was left alone.", backup, original, false);
        }

        if (!ReadsBack(path, verify))
        {
            if (backup is { } restore) File.Copy(restore.ToString(), path, overwrite: true);
            else File.Delete(path);
            return new SteamConfigEdit(mismatchRefusal ?? $"Steam's {path} did not read back what was written, so the original was restored.", backup, original, false);
        }

        return new SteamConfigEdit(null, backup, original, SteamStartedDuringWrite: tracker.IsSteamRunning());
    }

    private static bool ReadsBack(string path, Func<byte[], bool> verify)
    {
        try { return verify(File.ReadAllBytes(path)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    private static SteamConfigEdit Refuse(string refusal) => new(refusal, null, null, false);

    /// <summary>
    /// Writes beside the file and renames over it, so a crash never leaves half a file. The original's
    /// permissions carry over.
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes, bool carryMode)
    {
        var temp = path + TempSuffix;
        File.WriteAllBytes(temp, bytes);
        if (carryMode) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        File.Move(temp, path, overwrite: true);
    }
}
