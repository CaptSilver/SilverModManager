using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Saves;

/// <summary>Where a copy of a save sits under the saves folder.</summary>
public enum SaveBackupLocation
{
    /// <summary><c>backups/</c>: copies the user took, the copies taken before a restore, and the files a modded session changed.</summary>
    Backups,

    /// <summary><c>quarantine-backup/</c>: the copy taken just before a save was moved out of the prefix.</summary>
    QuarantineBackup,

    /// <summary><c>quarantine/</c>: the saves that were moved out of the prefix.</summary>
    Quarantined,
}

/// <param name="Path">The full path of the copy.</param>
/// <param name="SaveName">The save it is a copy of, such as <c>ER0000.co2</c> for Elden Ring.</param>
/// <param name="Stamp">When it was taken, <c>yyyyMMdd-HHmmss</c> (a copy kept by a modded session carries that session's stamp instead).</param>
/// <param name="Label">The text after the stamp, empty when there is none.</param>
public sealed record SaveBackup(string Path, string SaveName, string Stamp, string Label, SaveBackupLocation Location);

/// <param name="Destination">The save file that was replaced.</param>
/// <param name="PreRestoreBackup">The copy of what was there before, absent when the destination did not exist.</param>
public sealed record RestoreResult(string Destination, SaveBackup? PreRestoreBackup);

/// <summary>
/// Copies of an installation's saves, under <c>&lt;FromSoft data root&gt;/&lt;game&gt;/&lt;installation&gt;/saves/</c>, beside the
/// ledger of the launch gate. A copy is named <c>&lt;save&gt;.&lt;yyyyMMdd-HHmmss&gt;[-label]</c>.
/// </summary>
/// <remarks>
/// Only files <see cref="SaveFolderScanner"/> knows are copied or restored; anything else is refused. A restore
/// writes to the exact save the copy was taken from: a copy of the modded save never lands on the vanilla
/// save, which is the file a modded session must not change.
/// </remarks>
public sealed partial class SaveBackups(AbsolutePath root, SaveFolderScanner scanner, TimeProvider clock)
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    // The stamp of a user copy, or of a modded session's (yyyyMMddTHHmmssfffZ); then an optional label.
    [GeneratedRegex(@"^(?<save>.+?)\.(?<stamp>\d{8}-\d{6}|\d{8}T\d{9}Z)(?:-(?<label>.+))?$")]
    private static partial Regex NamePattern();

    public static SaveBackups For(ISettingsManager settings, IFileSystem fileSystem, GameInstallation installation, TimeProvider? clock = null)
    {
        var game = FromSoftGameOf(installation);
        return new SaveBackups(
            FromSoftPaths.InstallationRoot(game.Layout, settings.Get<FromSoftSettings>(), fileSystem, FromSoftPaths.InstallationFolderName(game.Layout, installation.LocatorResult)) / "saves",
            new SaveFolderScanner(game.SaveNames),
            clock ?? TimeProvider.System);
    }

    /// <summary>The installation's game, which has to be a FromSoft game: only those keep saves here.</summary>
    internal static IFromSoftGame FromSoftGameOf(GameInstallation installation)
        => installation.Game as IFromSoftGame
           ?? throw new InvalidOperationException($"{installation.Game.DisplayName} is not a FromSoft game, so it has no saves folder here.");

    public SaveFolderScanner Scanner => scanner;

    public AbsolutePath Root => root;

    internal string DirectoryOf(SaveBackupLocation location) => location switch
    {
        SaveBackupLocation.Backups => (root / "backups").GetFullPath(),
        SaveBackupLocation.QuarantineBackup => (root / "quarantine-backup").GetFullPath(),
        _ => (root / "quarantine").GetFullPath(),
    };

    /// <summary>Copies a known save into <c>backups/</c>.</summary>
    public SaveBackup Backup(string savePath, string label = "") => CopyInto(SaveBackupLocation.Backups, savePath, label, StampNow());

    internal string StampNow() => clock.GetLocalNow().ToString(StampFormat);

    /// <summary>
    /// Copies the save into the location. A copy never replaces another: when the name is taken the stamp moves on
    /// a second, so two copies in the same second (a manual one and a pre-restore one) both survive.
    /// </summary>
    internal SaveBackup CopyInto(SaveBackupLocation location, string savePath, string label, string stamp)
    {
        var name = Path.GetFileName(savePath);
        if (scanner.KindOf(name, out _) == SaveKind.Unknown)
            throw new SaveRefusedException($"{name} is not a save file this page knows, so it was left alone.");
        if (!File.Exists(savePath)) throw new SaveRefusedException($"{name} does not exist, so there is nothing to back up.");

        var directory = DirectoryOf(location);
        Directory.CreateDirectory(directory);

        var suffix = label.Length == 0 ? "" : $"-{label}";
        var destination = Path.Combine(directory, $"{name}.{stamp}{suffix}");
        for (var next = DateTime.ParseExact(stamp, StampFormat, null); File.Exists(destination); )
        {
            next = next.AddSeconds(1);
            stamp = next.ToString(StampFormat);
            destination = Path.Combine(directory, $"{name}.{stamp}{suffix}");
        }

        File.Copy(savePath, destination, overwrite: false);
        return new SaveBackup(destination, name, stamp, label, location);
    }

    /// <summary>
    /// Every copy under <c>backups/</c>, <c>quarantine-backup/</c> and <c>quarantine/</c>, newest first. A file in
    /// those folders whose name does not read as a copy of a known save is not listed.
    /// </summary>
    public IReadOnlyList<SaveBackup> List()
    {
        var found = new List<SaveBackup>();
        foreach (var location in Enum.GetValues<SaveBackupLocation>())
        {
            var directory = DirectoryOf(location);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Parse(file, location) is { } backup) found.Add(backup);
            }
        }

        return found.OrderByDescending(b => b.Stamp, StringComparer.Ordinal).ThenBy(b => b.Path, StringComparer.Ordinal).ToArray();
    }

    private SaveBackup? Parse(string path, SaveBackupLocation location)
    {
        var match = NamePattern().Match(Path.GetFileName(path));
        if (!match.Success) return null;

        var save = match.Groups["save"].Value;
        return scanner.KindOf(save, out _) == SaveKind.Unknown
            ? null
            : new SaveBackup(path, save, match.Groups["stamp"].Value, match.Groups["label"].Value, location);
    }

    /// <summary>
    /// Puts the copy back into the save folder as the save it was taken from. What is there now is copied to
    /// <c>backups/</c> first, labelled <c>pre-restore</c>; the copy is then written beside the save and renamed over it,
    /// so a failure leaves the save as it was.
    /// </summary>
    public RestoreResult Restore(SaveBackup backup, string saveFolder)
    {
        if (scanner.KindOf(backup.SaveName, out _) == SaveKind.Unknown)
            throw new SaveRefusedException($"{Path.GetFileName(backup.Path)} is not a copy of a save this page knows, so it was not restored.");
        if (!File.Exists(backup.Path)) throw new SaveRefusedException($"{Path.GetFileName(backup.Path)} no longer exists.");
        if (!Directory.Exists(saveFolder)) throw new SaveRefusedException($"The save folder {saveFolder} does not exist.");

        var destination = Path.Combine(saveFolder, backup.SaveName);
        var preRestore = File.Exists(destination) ? CopyInto(SaveBackupLocation.Backups, destination, "pre-restore", StampNow()) : null;

        // A fixed staging name could be a file the user put there; a fresh one never replaces anything.
        var temporary = $"{destination}.restore-{Guid.NewGuid():N}";
        try
        {
            File.Copy(backup.Path, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return new RestoreResult(destination, preRestore);
    }
}
