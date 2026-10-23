namespace SilverModManager.Games.FromSoft.EldenRing;

/// <summary>The launch tool of Elden Ring; see <see cref="RunFromSoftGame{TGame}"/>.</summary>
public class RunEldenRingGame(IServiceProvider provider, EldenRingGame game) : RunFromSoftGame<EldenRingGame>(provider, game);
