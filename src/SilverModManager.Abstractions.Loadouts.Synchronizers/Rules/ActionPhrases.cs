namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;

/// <summary>
/// Plain-words names for <see cref="Actions"/>, for log lines. A flag combination is composed from one word per flag,
/// in flag order, so a new combination never needs its own entry.
/// </summary>
public static class ActionPhrases
{
    private const string NoAction = "no action";
    private const string UnnamedFlag = "unknown action";

    // Flag order is the rendering order. Keep one entry per Actions member; a test enforces it.
    private static readonly (Actions Flag, string Phrase)[] Words =
    [
        (Actions.BackupFile, "back up"),
        (Actions.IngestFromDisk, "ingest"),
        (Actions.AdaptLoadout, "adapt the loadout"),
        (Actions.AddReifiedDelete, "record a delete"),
        (Actions.DeleteFromDisk, "delete"),
        (Actions.ExtractToDisk, "extract"),
        (Actions.WriteIntrinsic, "write"),
        (Actions.WarnOfUnableToExtract, "warn of a missing archive"),
        (Actions.WarnOfConflict, "warn of a conflict"),
        (Actions.ForgetDiskState, "stop tracking"),
    ];

    /// <summary>
    /// "back up and delete" for <c>BackupFile | DeleteFromDisk</c>; "no action" for nothing or <see cref="Actions.DoNothing"/>.
    /// </summary>
    public static string Describe(Actions actions)
    {
        var remaining = actions & ~Actions.DoNothing;
        var phrases = new List<string>();
        foreach (var (flag, phrase) in Words)
        {
            if ((remaining & flag) == 0) continue;
            phrases.Add(phrase);
            remaining &= ~flag;
        }

        if (remaining != 0) phrases.Add(UnnamedFlag);

        return phrases.Count switch
        {
            0 => NoAction,
            1 => phrases[0],
            _ => $"{string.Join(", ", phrases.Take(phrases.Count - 1))} and {phrases[^1]}",
        };
    }

    /// <summary>
    /// "phrase: count" per distinct phrase, biggest group first. Groups of equal size are ordered by phrase, so the
    /// same pending set always logs the same line.
    /// </summary>
    public static IEnumerable<string> Summarize(IEnumerable<Actions> actions) =>
        actions.GroupBy(Describe)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count()}");
}
