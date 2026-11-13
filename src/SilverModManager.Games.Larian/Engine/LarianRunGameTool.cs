using SilverModManager.Abstractions.Games;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>Starts a Larian game. The primary file comes from the game's own <c>GetPrimaryFile</c>.</summary>
public class LarianRunGameTool<TGame>(IServiceProvider serviceProvider, TGame game) : RunGameTool<TGame>(serviceProvider, game)
    where TGame : IGame;
