using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Saves;

/// <param name="MovedTo">Where the save went, absent when it was not in the folder.</param>
/// <param name="Notes">What happened and what is left for the user, in order, including the Steam Cloud instructions.</param>
public sealed record QuarantineResult(string? MovedTo, IReadOnlyList<string> Notes);

/// <summary>
/// Moves the vanilla save out of the prefix so a session starts from no save, for the case where a modded session
/// marked it. Steam Cloud may hold a copy that would sync straight back, so the result carries what to delete there.
/// </summary>
public sealed class SaveQuarantine(SaveBackups backups, IProcessTracker processTracker)
{
    /// <summary>
    /// Refuses, before anything on disk changes, while Steam runs. Otherwise copies the save to
    /// <c>quarantine-backup/</c> with the label <c>orig</c> and then moves it to <c>quarantine/</c>.
    /// </summary>
    /// <param name="savePath">The vanilla save of one account folder.</param>
    /// <param name="cloudSaves">What Steam Cloud holds; the entries for this account's folder and this file are reported.</param>
    public QuarantineResult Quarantine(string savePath, IReadOnlyList<CloudSave> cloudSaves)
    {
        if (processTracker.IsSteamRunning())
            throw new SaveRefusedException("Close Steam before quarantining the save; it may sync the save back from Steam Cloud.");

        var name = Path.GetFileName(savePath);
        if (backups.Scanner.KindOf(name, out var isBackup) != SaveKind.Vanilla || isBackup)
            throw new SaveRefusedException($"Only the vanilla save can be quarantined, and {name} is not it.");

        var notes = new List<string>();
        string? movedTo = null;
        if (File.Exists(savePath))
        {
            var copy = backups.CopyInto(SaveBackupLocation.QuarantineBackup, savePath, "orig", backups.StampNow());
            var quarantine = backups.DirectoryOf(SaveBackupLocation.Quarantined);
            Directory.CreateDirectory(quarantine);
            movedTo = Path.Combine(quarantine, $"{name}.{copy.Stamp}");
            File.Move(savePath, movedTo);
            notes.Add($"Moved {name} out of the prefix to {movedTo}; a copy is in {copy.Path}.");
        }
        else
        {
            notes.Add($"{name} is not in the folder (already quarantined?).");
        }

        var account = Path.GetFileName(Path.GetDirectoryName(savePath));
        notes.AddRange(cloudSaves
            .Where(c => c.SteamId64 == account && c.FileName.StartsWith(name, StringComparison.Ordinal))
            .Select(c => c.PurgeInstruction));
        return new QuarantineResult(movedTo, notes);
    }
}
