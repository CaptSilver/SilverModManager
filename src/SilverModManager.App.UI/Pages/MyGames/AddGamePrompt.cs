using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.Pages.MyGames;

/// <summary>
/// What to tell the player after they pointed My Games at a folder, and which games to offer when the folder
/// alone does not say which one it is.
/// </summary>
/// <param name="Candidates">One entry per game the folder could be; empty unless the outcome was ambiguous.</param>
public sealed record AddGamePrompt(string Title, string Markdown, IReadOnlyList<AddGamePrompt.Candidate> Candidates)
{
    /// <summary>A game the folder could be an install of.</summary>
    public sealed record Candidate(string Label, GameId Game);

    public static AddGamePrompt For(AddGameResult result, AbsolutePath folder)
    {
        return result switch
        {
            AddGameResult.Added added => Plain(
                $"{added.Game.DisplayName} added",
                $"""
                {added.Game.DisplayName} is recorded and is listed under My Games.

                Press **Add** on its card to start managing it. If that is refused, the reason is shown there.
                """
            ),
            AddGameResult.NeedsPrefix needsPrefix => Plain(
                $"{needsPrefix.Game.DisplayName} needs a Wine prefix",
                $"""
                `{folder}` is a Windows build of {needsPrefix.Game.DisplayName}, which needs a Wine prefix to run. Nothing was added.

                Add it from a terminal and name the prefix:

                `silvermod add-game "{folder}" --prefix <prefix>`
                """
            ),
            AddGameResult.Ambiguous ambiguous => new AddGamePrompt(
                "Which game is this?",
                $"""
                Several games fit `{folder}` and nothing in it says which one this is. Pick the game to add it as.

                {string.Join(Environment.NewLine, ambiguous.Candidates.Select(candidate => $"- {candidate.Game.DisplayName} launches `{candidate.File}`"))}
                """,
                ambiguous.Candidates.Select(candidate => new Candidate(candidate.Game.DisplayName, candidate.Game.GameId)).ToArray()
            ),
            AddGameResult.FolderUnreadable unreadable => Plain(
                "Couldn't read the folder",
                $"`{unreadable.Folder}` could not be listed: {unreadable.Message}"
            ),
            AddGameResult.AlreadyDetected detected => Plain(
                $"{detected.Game.DisplayName} is already listed",
                $"`{folder}` is already reported as {detected.Game.DisplayName} from {detected.Store.Value}. Nothing was added."
            ),
            AddGameResult.Unknown => Plain(
                "No known game found",
                $"No game the app knows launches from `{folder}`. Pick the folder that holds the game's executable."
            ),
            AddGameResult.StoreUnavailable unavailable => Plain(
                "Couldn't record the game",
                unavailable.Message
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unhandled add-game outcome"),
        };
    }

    private static AddGamePrompt Plain(string title, string markdown) => new(title, markdown, []);
}
