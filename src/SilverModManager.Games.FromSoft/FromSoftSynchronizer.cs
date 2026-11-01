namespace SilverModManager.Games.FromSoft;

/// <summary>
/// The synchronizer of a FromSoft game that adds nothing to <see cref="AFromSoftSynchronizer"/> but its loader's files.
/// </summary>
public sealed class FromSoftSynchronizer(IServiceProvider provider, IFromSoftGame game)
    : AFromSoftSynchronizer(provider, game, game.Loader.IntrinsicFiles());
