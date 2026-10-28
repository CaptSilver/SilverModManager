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
    public static CleanFolderPrompt For(bool buildCovered, int fileCount, string gameName, UnbackedFiles unbacked)
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
        return new CleanFolderPrompt(
            $"Your {gameName} folder isn't a clean install",
            $"""
            We found {fileCount} files in the game folder that aren’t part of a clean install. To avoid conflicts with mods, **we recommend starting with a clean folder**.
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
