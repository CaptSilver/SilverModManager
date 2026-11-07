using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What to tell the player when a game that was just added has files in its folder that are not part of the
/// vanilla install, and whether cleaning the folder is on offer at all.
/// </summary>
/// <param name="OffersClean">False when the build is not covered by the file hashes data: cleaning deletes by the
/// vanilla file list, which for such a build would delete the game's own files.</param>
public sealed record CleanFolderPrompt(string Title, string Markdown, bool OffersClean)
{
    private const int MaxListed = 8;

    /// <param name="fileCount">The files a clean would move, not counting the game's own files that are missing.</param>
    /// <param name="filePaths">The game-folder paths of the files a clean would move, so the prompt can say what they are.</param>
    /// <param name="missingGameFiles">How many of the game's own files are gone from the folder. A clean puts them back, so they are not among the files it moves.</param>
    public static CleanFolderPrompt For(bool buildCovered, int fileCount, string gameName, UnbackedFiles unbacked, IReadOnlyCollection<string>? filePaths = null, int missingGameFiles = 0)
    {
        if (!buildCovered)
        {
            return new CleanFolderPrompt(
                $"{gameName}: this build isn't recognised yet",
                """
                The app doesn't have file data for the installed build, so it can't tell the game's own files from added ones. The existing files are kept as **External Changes**, and the app will not delete them when you clean or remove the game.
                """,
                OffersClean: false
            );
        }

        var backupLine = unbacked.Count > 0 ? UnbackedFilesNote(unbacked) + "\n\n" : "";
        var missingLine = missingGameFiles > 0
            ? $"{Files(missingGameFiles)} the game ships {(missingGameFiles == 1 ? "is" : "are")} missing from the folder. **Clean folder** puts {(missingGameFiles == 1 ? "it" : "them")} back."
            : "";

        // Nothing to move: the changes are all missing game files, so there is no file count to announce and nothing
        // to say about the found-in-the-game-folder group.
        if (fileCount == 0 && missingGameFiles > 0)
        {
            return new CleanFolderPrompt(
                $"Your {gameName} folder isn't a clean install",
                $"""
                {missingLine} To avoid conflicts with mods, **we recommend starting with a clean folder**.
                #### Important
                If you keep existing files (not recommended):
                - The existing files will be placed in **External Changes**.
                - Files in External Changes **override any mods you install later**.
                - If you stop managing this game or uninstall the app, those **files will be permanently removed**.

                {backupLine}Content the game installed itself (Creations, the in-game mod manager) is kept either way.
                """,
                OffersClean: true
            );
        }

        var missingParagraph = missingLine.Length > 0 ? $"\n{missingLine}\n" : "";
        return new CleanFolderPrompt(
            $"Your {gameName} folder isn't a clean install",
            $"""
            We found {Files(fileCount)} in the game folder that aren’t part of a clean install. To avoid conflicts with mods, **we recommend starting with a clean folder**.
            {WhatIsThere(filePaths)}{missingParagraph}
            **Clean folder** doesn't delete anything: it moves these files into **{DefaultCollection.FoundInGameFolderName}**, a switched off group in My Mods. Clean folder takes them out of the game folder now; switch the group back on to put them back.
            #### Important
            If you keep existing files (not recommended):
            - The existing files will be placed in **External Changes**.
            - Files in External Changes **override any mods you install later**.
            - If you stop managing this game or uninstall the app, those **files will be permanently removed**.

            {backupLine}Content the game installed itself (Creations, the in-game mod manager) is kept either way.
            """,
            OffersClean: true
        );
    }

    /// <summary>
    /// The folders (or loose files) the files sit in, biggest first, so the player can tell a mod folder from a log.
    /// A folder that holds one subfolder and nothing else is followed down, so a mod buried under
    /// <c>Phoenix/Content/Paks/~mods</c> is named rather than hidden behind <c>Phoenix</c>. Empty when the paths are not known.
    /// </summary>
    private static string WhatIsThere(IReadOnlyCollection<string>? filePaths)
    {
        if (filePaths is null || filePaths.Count == 0) return "";

        var tops = filePaths.GroupBy(FirstSegment, StringComparer.Ordinal)
            .SelectMany(group => Rows(group.Key, group.ToArray()))
            .OrderByDescending(top => top.Count).ThenBy(top => top.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lines = tops.Take(MaxListed).Select(top => top.IsFolder ? $"- {top.Name} ({Files(top.Count)})" : $"- {top.Name}").ToList();
        if (tops.Length > MaxListed) lines.Add($"- and {tops.Length - MaxListed} more");
        return "\nThey are in:\n" + string.Join('\n', lines) + "\n";
    }

    private static string FirstSegment(string path) => path.Contains('/') ? path[..path.IndexOf('/')] : path;

    /// <summary>
    /// The rows for one top-level entry. A loose file is its own row. A folder is followed down while it holds a single
    /// subfolder and nothing else; where it then splits into several subfolders and holds no loose file, each of those
    /// is a row of its own (the mods, in a mods folder).
    /// </summary>
    private static IEnumerable<(string Name, int Count, bool IsFolder)> Rows(string name, string[] paths)
    {
        if (!paths.Any(path => path.Contains('/')))
        {
            yield return (name, paths.Length, false);
            yield break;
        }

        var (folder, inside) = FollowSingleSubfolders(name, paths.Select(path => path[(name.Length + 1)..]).ToArray());
        if (inside.All(path => path.Contains('/')))
        {
            foreach (var child in inside.GroupBy(FirstSegment, StringComparer.Ordinal))
            {
                var (childName, _) = FollowSingleSubfolders($"{folder}/{child.Key}", child.Select(path => path[(child.Key.Length + 1)..]).ToArray());
                yield return (childName, child.Count(), true);
            }
        }
        else
        {
            yield return (folder, inside.Length, true);
        }
    }

    /// <summary>Appends the subfolder to the name while every path goes through the same one, returning the paths relative to where it stopped.</summary>
    private static (string Name, string[] Inside) FollowSingleSubfolders(string name, string[] inside)
    {
        while (inside.All(path => path.Contains('/')) && inside.Select(FirstSegment).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            var segment = FirstSegment(inside[0]);
            name = $"{name}/{segment}";
            inside = inside.Select(path => path[(segment.Length + 1)..]).ToArray();
        }

        return (name, inside);
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>
    /// What cleaning costs when some files have no copy in the app's backup: they are copied first, and past the cap
    /// the app asks before it copies.
    /// </summary>
    public static string UnbackedFilesNote(UnbackedFiles unbacked) =>
        $"{unbacked.Count} of them ({unbacked.Total}) have no backup copy. Cleaning copies them into the app's backup first, then removes them from the folder; over {BackupLimit.Default.Maximum} the app asks before it copies.";

    /// <summary>
    /// What removing a game costs when some files have no copy in the app's backup. Removing drops the backup it
    /// makes on the way out, so unlike cleaning, nothing is left to restore from.
    /// </summary>
    public static string RemoveGameNote(UnbackedFiles unbacked) =>
        $"{unbacked.Count} files ({unbacked.Total}) in this game's loadouts have no backup copy. Removing the game deletes them from the folder and they cannot be restored afterwards.";
}
