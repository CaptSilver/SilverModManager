using Humanizer;
using Humanizer.Bytes;
using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// What the player was doing when the backup-cap question came up. The wording of the dialog, its cancel button and
/// the command line hint in a refusal all depend on it: "Don't manage" means nothing to someone who pressed Play.
/// </summary>
[PublicAPI]
public enum BackupOverflowOperation
{
    /// <summary>Adding a game: the first sync of a new loadout.</summary>
    Manage,

    /// <summary>Applying a loadout to the game folder.</summary>
    Apply,

    /// <summary>Launching the game, which applies the loadout first.</summary>
    Launch,

    /// <summary>Switching to another loadout.</summary>
    Switch,

    /// <summary>Putting the game folder back to its original state (clean, remove the game, delete a loadout).</summary>
    Reset,
}

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
public sealed record BackupOverflow(Size Adoptable, int AdoptableCount, Size MustBackUp, int MustBackUpCount, Size Limit, BackupOverflowOperation Operation = BackupOverflowOperation.Apply)
{
    /// <summary>Bytes of every file that would be backed up.</summary>
    public Size Total => Adoptable + MustBackUp;

    /// <summary>Number of files that would be backed up.</summary>
    public int FileCount => AdoptableCount + MustBackUpCount;

    /// <summary>
    /// A size as the rest of the app shows one: binary units, one decimal from a gigabyte up. The dialog, the
    /// refusal and the log all go through this so the same bytes read the same everywhere.
    /// </summary>
    public static string FormatSize(Size size)
    {
        var bytes = ByteSize.FromBytes(size.Value);
        return bytes.Gigabytes < 1 ? bytes.Humanize("0") : bytes.Humanize("0.0");
    }

    /// <summary>Whether adopting in place leaves a backup that fits under the cap.</summary>
    public bool CanAdopt => AdoptableCount > 0 && MustBackUp <= Limit;

    /// <summary><see cref="Total"/> as <see cref="FormatSize"/> prints it.</summary>
    public string TotalText => FormatSize(Total);

    /// <summary><see cref="Limit"/> as <see cref="FormatSize"/> prints it.</summary>
    public string LimitText => FormatSize(Limit);

    /// <summary><see cref="MustBackUp"/> as <see cref="FormatSize"/> prints it.</summary>
    public string MustBackUpText => FormatSize(MustBackUp);

    /// <summary><see cref="Adoptable"/> as <see cref="FormatSize"/> prints it.</summary>
    public string AdoptableText => FormatSize(Adoptable);
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
        : base(Describe(installation, overflow))
    {
        _installation = installation;
        Overflow = overflow;
    }

    // The CLI can only answer the question for a verb that takes --large-files, so the hint names the verb that
    // matches what was being done. A switch or a reset has no such verb; backing the files up first is the way out.
    private static string Describe(GameInstallation installation, BackupOverflow overflow)
    {
        var game = installation.Game;
        var intro =
            $"{overflow.FileCount} files ({overflow.TotalText}) in the {game.DisplayName} folder are not part of a clean install. " +
            $"The app backs up at most {overflow.LimitText} of such files before it {overflow.Operation switch
            {
                BackupOverflowOperation.Manage => "manages a game",
                BackupOverflowOperation.Apply => "applies a loadout",
                BackupOverflowOperation.Launch => "launches a modded game",
                BackupOverflowOperation.Switch => "switches loadouts",
                _ => "puts the game folder back",
            }}.\n\n";

        const string backupVerb = "`silvermod loadout backup-game-files` backs them up at any time.";
        const string adoptNote =
            "Adopted files stay where they are and are not copied now; " +
            "switching loadouts, cleaning or removing the game backs them up first and asks again when that is over the cap. " +
            "A file deleted outside the app is the one case the app cannot restore. ";

        return intro + overflow.Operation switch
        {
            BackupOverflowOperation.Manage =>
                $"Run `silvermod loadout create --game \"{game.GameId}\" --large-files backup` to back them all up, " +
                "or `--large-files adopt` to manage them where they are. " + adoptNote + backupVerb,
            BackupOverflowOperation.Apply =>
                "Run `silvermod loadout synchronize --loadout <loadout> --large-files backup` to back them all up, " +
                "or `--large-files adopt` to leave them where they are. " + adoptNote + backupVerb,
            BackupOverflowOperation.Launch =>
                "Apply the loadout first with `silvermod loadout synchronize --loadout <loadout> --large-files backup` " +
                "(or `--large-files adopt`), then launch again. " + backupVerb,
            _ => "Back them up first, then try again. " + backupVerb,
        };
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
        $"{overflow.MustBackUpCount} of these files ({overflow.MustBackUpText}) are about to be deleted or replaced, so adopting them in place would lose them. " +
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
