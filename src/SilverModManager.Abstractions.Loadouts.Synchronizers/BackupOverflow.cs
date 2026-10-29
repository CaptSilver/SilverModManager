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

    /// <summary>Taking in what the game wrote to its folder while it ran, after it exited.</summary>
    AfterPlay,
}

/// <summary>
/// What a file the sync would back up is, in the terms a player knows. Chosen from the file's sync state, not from
/// where the loadout says it came from: a file that exists only on disk has no loadout source at all.
/// </summary>
[PublicAPI]
public enum BackupOverflowKind : byte
{
    /// <summary>On disk only: in neither the loadout nor anything applied before. You, Steam or another program put it there.</summary>
    Appeared,

    /// <summary>A game file of the loadout whose bytes on disk no longer match what the app saw. A game update or repair does this.</summary>
    GameFileChanged,

    /// <summary>A mod's file whose bytes on disk changed since the app put it there.</summary>
    ModFileChanged,

    /// <summary>
    /// A file this operation deletes, replaces or rewrites, or one that already matches the loadout and is taken
    /// under management: the copy is made whatever the player answers.
    /// </summary>
    MustBackUp,
}

/// <summary>The files of one kind under one folder.</summary>
/// <param name="Folder">The folder as the player sees it: the location's first two path segments, or <see cref="BackupOverflow.GameFolderName"/>.</param>
/// <param name="Location">The location's player-facing name ("Saves", "Settings"), or null for the game folder.</param>
[PublicAPI]
public sealed record BackupOverflowGroup(string Folder, int FileCount, Size Size, BackupOverflowKind Kind, string? Location = null);

/// <summary>One of the biggest files in the overflow.</summary>
[PublicAPI]
public sealed record BackupOverflowExample(string Path, Size Size, BackupOverflowKind Kind);

/// <summary>
/// The files a sync is about to back up when their total is over the cap.
/// </summary>
/// <remarks>
/// Adoptable: the bytes of nodes whose action set is exactly BackupFile | IngestFromDisk, i.e. files that stay
/// on disk as they are and only want a copy. MustBackUp: the bytes of every other node that carries BackupFile
/// (an intrinsic file about to be rewritten, a file about to be deleted or replaced). Keeping the first set where
/// it is strips the backup from that set only, so <see cref="CanAdopt"/> asks whether the second set fits under the
/// cap on its own.
/// </remarks>
[PublicAPI]
public sealed record BackupOverflow(
    Size Adoptable,
    int AdoptableCount,
    Size MustBackUp,
    int MustBackUpCount,
    Size Limit,
    BackupOverflowOperation Operation = BackupOverflowOperation.Apply,
    IReadOnlyList<BackupOverflowGroup>? Groups = null,
    IReadOnlyList<BackupOverflowExample>? Examples = null,
    IReadOnlyDictionary<BackupOverflowKind, (int Count, Size Size)>? ByKind = null)
{
    /// <summary>How many folders the dialog lists before it folds the rest into one line.</summary>
    public const int ListedFolders = 5;

    /// <summary>The folder name for files directly in the game folder.</summary>
    public const string GameFolderName = "(game folder)";

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

    /// <summary>Whether keeping the files in place leaves a backup that fits under the cap.</summary>
    public bool CanAdopt => AdoptableCount > 0 && MustBackUp <= Limit;

    /// <summary><see cref="Total"/> as <see cref="FormatSize"/> prints it.</summary>
    public string TotalText => FormatSize(Total);

    /// <summary><see cref="Limit"/> as <see cref="FormatSize"/> prints it.</summary>
    public string LimitText => FormatSize(Limit);

    /// <summary><see cref="MustBackUp"/> as <see cref="FormatSize"/> prints it.</summary>
    public string MustBackUpText => FormatSize(MustBackUp);

    /// <summary><see cref="Adoptable"/> as <see cref="FormatSize"/> prints it.</summary>
    public string AdoptableText => FormatSize(Adoptable);

    /// <summary>
    /// The files per kind. A tree measured without kinds (a hand-built overflow) has the two sets it always had:
    /// files that only want a copy did not come from the app, and the others are copied whatever the answer.
    /// </summary>
    public IReadOnlyDictionary<BackupOverflowKind, (int Count, Size Size)> Kinds
    {
        get
        {
            if (ByKind is not null) return ByKind;
            var derived = new Dictionary<BackupOverflowKind, (int, Size)>();
            if (AdoptableCount > 0) derived[BackupOverflowKind.Appeared] = (AdoptableCount, Adoptable);
            if (MustBackUpCount > 0) derived[BackupOverflowKind.MustBackUp] = (MustBackUpCount, MustBackUp);
            return derived;
        }
    }

    /// <summary>The kinds present, biggest first.</summary>
    public IReadOnlyList<BackupOverflowKind> KindsBySize
        => Kinds.OrderByDescending(k => k.Value.Size.Value).ThenBy(k => k.Key).Select(k => k.Key).ToArray();

    /// <summary>True when every file is of one kind.</summary>
    public bool IsSingleKind => Kinds.Count <= 1;

    /// <summary>The folders to list: the biggest <see cref="ListedFolders"/>, biggest first.</summary>
    public IReadOnlyList<BackupOverflowGroup> ListedGroups
        => (Groups ?? []).OrderByDescending(g => g.Size.Value).ThenBy(g => g.Folder, StringComparer.Ordinal).Take(ListedFolders).ToArray();

    /// <summary>How many folders the list leaves out.</summary>
    public int HiddenFolders => Math.Max(0, (Groups?.Count ?? 0) - ListedFolders);

    /// <summary>What the folders left out of the list hold.</summary>
    public Size HiddenSize => Size.FromLong((long)((Groups ?? []).Sum(g => (long)g.Size.Value) - ListedGroups.Sum(g => (long)g.Size.Value)));

    /// <summary>
    /// What the player was doing, as an "-ing" phrase for "by ..." and "because ...": what the dialog says deletes,
    /// replaces or changes the files.
    /// </summary>
    public string OperationPhrase => Operation switch
    {
        BackupOverflowOperation.Manage => "adding the game",
        BackupOverflowOperation.Apply => "applying the loadout",
        BackupOverflowOperation.Launch => "starting the game",
        BackupOverflowOperation.Switch => "switching loadouts",
        BackupOverflowOperation.Reset => "putting the folder back",
        _ => "keeping what the game wrote",
    };

    /// <summary>"1 file" or "3 files".</summary>
    public static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>
    /// True when nothing in the overflow can stay where it is: every file is deleted or replaced by the operation,
    /// so the copy is the operation's own and not a favour to the player.
    /// </summary>
    public bool OnlyMustBackUp => AdoptableCount == 0;

    /// <summary>
    /// The first paragraph: whose files, how many, and what they are in a player's words. Never calls them mods.
    /// </summary>
    public string Opening(string gameName)
    {
        if (Operation == BackupOverflowOperation.AfterPlay)
            return $"While {gameName} ran, {Files(FileCount)} ({TotalText}) appeared or changed. {WhatTheyAre()}";

        if (!OnlyMustBackUp)
            return $"{gameName} has {Files(FileCount)} ({TotalText}) that the app has no copy of. {WhatTheyAre()}";

        var subject = Operation switch
        {
            BackupOverflowOperation.Manage => $"Adding {gameName}",
            BackupOverflowOperation.Apply => $"Applying this loadout to {gameName}",
            BackupOverflowOperation.Launch => $"Starting {gameName} with this loadout",
            BackupOverflowOperation.Switch => $"Switching {gameName} to another loadout",
            _ => $"Putting the {gameName} folder back",
        };
        var verb = Operation == BackupOverflowOperation.Reset ? "deletes" : "deletes or replaces";
        return $"{subject} {verb} {Files(FileCount)} ({TotalText}), and the app copies them first so it can put them back.";
    }

    /// <summary>The sentence that says what the files are, led by the biggest kind.</summary>
    public string WhatTheyAre()
    {
        var kinds = KindsBySize;
        if (kinds.Count == 0) return "";

        var lead = kinds[0] switch
        {
            BackupOverflowKind.Appeared => $"These are not from this app and not your mods: {WhoPutThem()} {WhereAppeared()}.",
            BackupOverflowKind.GameFileChanged => "These are the game's own files, new or changed since the app last saw them. A game update does that.",
            BackupOverflowKind.ModFileChanged => "These are mod files that changed since the app put them in place; something edited them.",
            _ => $"The app copies them first because {OperationPhrase} deletes, replaces or changes them.",
        };
        if (kinds.Count == 1) return lead;

        var second = kinds[1];
        var (count, size) = Kinds[second];
        var tail = second switch
        {
            BackupOverflowKind.Appeared => Operation == BackupOverflowOperation.AfterPlay ? "were written by the game or another program" : "were put there by you, Steam or another program",
            BackupOverflowKind.GameFileChanged => "are the game's own files, new or changed",
            BackupOverflowKind.ModFileChanged => "are mod files that changed",
            _ => $"are changed by {OperationPhrase}",
        };
        return $"{lead} {Files(count)} ({FormatSize(size)}) {tail}.";
    }

    private string WhoPutThem() => Operation == BackupOverflowOperation.AfterPlay
        ? "the game or another program wrote them"
        : "you, Steam or another program put them";

    /// <summary>"in the game folder", or the named places when any of the not-from-the-app files sit elsewhere.</summary>
    private string WhereAppeared()
    {
        var appeared = (Groups ?? []).Where(g => g.Kind == BackupOverflowKind.Appeared).ToArray();
        var places = new List<string>();
        if (appeared.Length == 0 || appeared.Any(g => g.Location is null)) places.Add("the game folder");
        places.AddRange(appeared.Select(g => g.Location).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var list = places.Count == 1 ? places[0] : string.Join(", ", places.Take(places.Count - 1)) + " and " + places[^1];
        return "in " + list;
    }

    /// <summary>
    /// The folder list in markdown, largest first, with the folders it leaves out and a few example files. Empty
    /// when the measurement carried no folders.
    /// </summary>
    public string FolderList()
    {
        var listed = ListedGroups;
        if (listed.Count == 0) return "";

        var mixed = !IsSingleKind;
        var lines = listed.Select(g => $"- {g.Folder} — {Files(g.FileCount)}, {FormatSize(g.Size)}{(mixed ? KindSuffix(g.Kind) : "")}");
        var text = "Where they are:\n\n" + string.Join('\n', lines);

        List<string> after = [];
        if (HiddenFolders > 0) after.Add($"and {HiddenFolders} more {(HiddenFolders == 1 ? "folder" : "folders")} ({FormatSize(HiddenSize)}).");
        if (Examples is { Count: > 0 })
            after.Add($"For example {string.Join(", ", Examples.Select(e => $"{e.Path} ({FormatSize(e.Size)})"))}.");
        return after.Count == 0 ? text : text + "\n\n" + string.Join(' ', after);
    }

    private string KindSuffix(BackupOverflowKind kind) => kind switch
    {
        BackupOverflowKind.Appeared => ", not from the app",
        BackupOverflowKind.GameFileChanged => ", game files",
        BackupOverflowKind.ModFileChanged => ", mod files",
        _ => ", copied first",
    };
}

/// <summary>
/// Asks the player what to do when the files to back up are over the backup cap.
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
/// The most bytes of such files the app backs up before it asks.
/// </summary>
public sealed record BackupLimit(Size Maximum)
{
    /// <summary>5 GB.</summary>
    public static BackupLimit Default => new(Size.GB * 5);
}

/// <summary>
/// Thrown, before anything is written, when files to back up are over the backup cap and the answer is to stop:
/// a refusing preset, a prompt that answered refuse, or keeping files in place that are about to be deleted.
/// </summary>
/// <remarks>
/// <see cref="Exception.Message"/> is written for a dialog and never names a command; the terminal gets
/// <see cref="CommandLineHint"/> on top of it.
/// </remarks>
[PublicAPI]
public sealed class BackupOverflowException : UserFacingException
{
    private const string BackupVerb = "`silvermod loadout backup-game-files` backs them up at any time.";

    private readonly GameInstallation _installation;
    private readonly string? _hint;

    /// <summary>The refusal for a preset or an answer that stops the operation.</summary>
    public BackupOverflowException(GameInstallation installation, BackupOverflow overflow)
        : base($"{overflow.Opening(installation.Game.DisplayName)}\n\nThe app copies such files by itself up to {overflow.LimitText}; this was over that, and nothing was changed.")
    {
        _installation = installation;
        Overflow = overflow;
    }

    private BackupOverflowException(GameInstallation installation, BackupOverflow overflow, string message, string hint) : base(message)
    {
        _installation = installation;
        Overflow = overflow;
        _hint = hint;
    }

    /// <summary>Keeping the files where they are would leave files that are about to be deleted or replaced without a copy.</summary>
    public static BackupOverflowException AdoptWouldLoseFiles(GameInstallation installation, BackupOverflow overflow) => new(
        installation,
        overflow,
        $"{BackupOverflow.Files(overflow.MustBackUpCount)} ({overflow.MustBackUpText}) of these are deleted or replaced by {overflow.OperationPhrase}, so keeping them where they are would lose them. " +
        "Back them up, or leave the folder as it is.",
        "Use --large-files backup.");

    /// <inheritdoc />
    public override string Title => Overflow.Operation == BackupOverflowOperation.Launch ? "The game was not started" : "Files were not backed up";

    /// <summary>
    /// The terminal's way out: the verb that matches what was being done, since only some verbs take
    /// <c>--large-files</c>. A switch or a reset has none; backing the files up first is the way out.
    /// </summary>
    public override string? CommandLineHint
    {
        get
        {
            if (_hint is not null) return _hint;

            var gameId = _installation.Game.GameId;
            return Overflow.Operation switch
            {
                BackupOverflowOperation.Manage =>
                    $"Run `silvermod loadout create --game \"{gameId}\" --large-files backup` to back them all up, or `--large-files adopt` to keep them where they are. {BackupVerb}",
                BackupOverflowOperation.Apply =>
                    $"Run `silvermod loadout synchronize --loadout <loadout> --large-files backup` to back them all up, or `--large-files adopt` to leave them where they are. {BackupVerb}",
                BackupOverflowOperation.Launch =>
                    $"Apply the loadout first with `silvermod loadout synchronize --loadout <loadout> --large-files backup` (or `--large-files adopt`), then launch again. {BackupVerb}",
                _ => $"Back them up first, then try again. {BackupVerb}",
            };
        }
    }

    /// <summary>What was measured.</summary>
    public BackupOverflow Overflow { get; }

    /// <summary>
    /// True when the player answered the question with refuse. The GUI skips its error dialog for that, since
    /// the player already knows.
    /// </summary>
    public bool Declined { get; init; }
}
