using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// The files a sync is about to back up when their total is over the cap.
/// </summary>
/// <remarks>
/// Adoptable: the bytes of nodes whose action set is exactly BackupFile | IngestFromDisk, i.e. files that stay
/// on disk as they are and only want a copy. MustBackUp: the bytes of every other node that carries BackupFile
/// (an intrinsic file about to be rewritten, a file about to be deleted or replaced). Adopting strips the backup
/// from the first set only, so <see cref="CanAdopt"/> asks whether the second set fits under the cap on its own.
/// </remarks>
[PublicAPI]
public sealed record BackupOverflow(Size Adoptable, int AdoptableCount, Size MustBackUp, int MustBackUpCount, Size Limit)
{
    /// <summary>Bytes of every file that would be backed up.</summary>
    public Size Total => Adoptable + MustBackUp;

    /// <summary>Number of files that would be backed up.</summary>
    public int FileCount => AdoptableCount + MustBackUpCount;

    /// <summary>Whether adopting in place leaves a backup that fits under the cap.</summary>
    public bool CanAdopt => AdoptableCount > 0 && MustBackUp <= Limit;
}

/// <summary>
/// Asks the player what to do when added files are over the backup cap.
/// </summary>
[PublicAPI]
public interface IBackupOverflowPrompt
{
    /// <summary>
    /// Asks, and ends with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled while the question is open.
    /// </summary>
    ValueTask<BackupOverflowChoice> Ask(GameInstallation installation, BackupOverflow overflow, CancellationToken cancellationToken);
}

/// <summary>
/// The most added-file bytes the app backs up before it asks.
/// </summary>
public sealed record BackupLimit(Size Maximum)
{
    /// <summary>5 GB.</summary>
    public static BackupLimit Default => new(Size.GB * 5);
}

/// <summary>
/// Thrown, before anything is written, when added files are over the backup cap and the answer is to stop:
/// a refusing preset, a prompt that answered refuse, or an adopt that would lose files about to be deleted.
/// </summary>
[PublicAPI]
public sealed class BackupOverflowException : UserFacingException
{
    private readonly GameInstallation _installation;

    /// <summary>The refusal that names the CLI flags.</summary>
    public BackupOverflowException(GameInstallation installation, BackupOverflow overflow)
        : base(
            $"{overflow.FileCount} files ({overflow.Total}) in the {installation.Game.DisplayName} folder are not part of a clean install. " +
            $"The app backs up at most {overflow.Limit} of such files before it manages a game.\n\n" +
            $"Run `silvermod loadout create --game \"{installation.Game.GameId}\" --large-files backup` to back them all up, " +
            "or `--large-files adopt` to manage them where they are. Adopted files stay where they are and are not copied now; " +
            "switching loadouts, cleaning or removing the game backs them up first and asks again when that is over the cap. " +
            "A file deleted outside the app is the one case the app cannot restore. " +
            "`silvermod loadout backup-game-files` backs them up at any time.")
    {
        _installation = installation;
        Overflow = overflow;
    }

    private BackupOverflowException(GameInstallation installation, BackupOverflow overflow, string message) : base(message)
    {
        _installation = installation;
        Overflow = overflow;
    }

    /// <summary>Adopting would leave files that are about to be deleted or replaced without a copy.</summary>
    public static BackupOverflowException AdoptWouldLoseFiles(GameInstallation installation, BackupOverflow overflow) => new(
        installation,
        overflow,
        $"{overflow.MustBackUpCount} of these files ({overflow.MustBackUp}) are about to be deleted or replaced, so adopting them in place would lose them. " +
        "Back them up (--large-files backup) or leave the folder as it is.");

    /// <inheritdoc />
    public override string Title => $"{_installation.Game.DisplayName} has more added files than the app backs up on its own";

    /// <summary>What was measured.</summary>
    public BackupOverflow Overflow { get; }

    /// <summary>
    /// True when the player answered the question with refuse. The GUI skips its error dialog for that, since
    /// the player already knows.
    /// </summary>
    public bool Declined { get; init; }
}
