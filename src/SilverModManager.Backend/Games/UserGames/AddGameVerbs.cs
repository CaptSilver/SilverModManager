using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Backend.Games.UserGames;

internal static class AddGameVerbs
{
    internal static IServiceCollection AddAddGameVerbs(this IServiceCollection services)
    {
        services.AddSingleton<IAddGameByPath, AddGameByPathService>();
        return services.AddVerb(() => AddGame);
    }

    // The prefix and wine binary are strings because the verb framework has no parser for an optional path.
    [Verb("add-game", "Records a game install in a folder, so it is listed in My Games")]
    private static async Task<int> AddGame(
        [Injected] IRenderer renderer,
        [Injected] IAddGameByPath service,
        [Injected] IFileSystem fileSystem,
        [Option("f", "folder", "The game's install folder")] AbsolutePath folder,
        [Option("g", "game", "The game id, when the folder fits more than one game", true)] string? game,
        [Option("p", "prefix", "The Wine prefix a Windows build runs in", true)] string? prefix,
        [Option("w", "wine", "The wine binary that goes with --prefix, when it is not the default one", true)] string? wine)
    {
        AbsolutePath? prefixPath = prefix is null ? null : fileSystem.FromUnsanitizedFullPath(prefix);
        AbsolutePath? winePath = wine is null ? null : fileSystem.FromUnsanitizedFullPath(wine);
        var result = service.Add(folder, game is null ? null : GameId.From(game), prefixPath, winePath);

        var (exitCode, line) = Describe(result, folder);
        await renderer.TextLine(line);
        return exitCode;
    }

    internal static (int ExitCode, string Line) Describe(AddGameResult result, AbsolutePath folder) => result switch
    {
        AddGameResult.Added added => (0, $"Added {added.Game.DisplayName} at {folder}; it is listed in My Games"),
        AddGameResult.NeedsPrefix needs => (1, $"{needs.Game.DisplayName} is a Windows build; pass --prefix <wine prefix> so it can run"),
        AddGameResult.Ambiguous ambiguous => (1, $"Several games fit {folder}: " +
                                                 string.Join(", ", ambiguous.Candidates.Select(candidate => $"{candidate.Game.GameId} ({candidate.Game.DisplayName}, launches {candidate.File.FileName})")) +
                                                 "; pass --game <id>"),
        AddGameResult.FolderUnreadable unreadable => (1, $"Could not list {unreadable.Folder}: {unreadable.Message}"),
        AddGameResult.AlreadyDetected detected => (1, $"{folder} is already detected as a {detected.Store} install of {detected.Game.DisplayName}"),
        AddGameResult.Unknown => (1, $"No known game launches from {folder}"),
        AddGameResult.StoreUnavailable unavailable => (1, unavailable.Message),
        _ => throw new NotSupportedException($"Unhandled add-game result {result.GetType()}"),
    };
}
