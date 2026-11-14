using JetBrains.Annotations;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// The store client that owns an installed game and can start it, under the name that client knows it by.
/// </summary>
/// <param name="Kind">Which client.</param>
/// <param name="AppName">The client's own key for the game: Steam's app id, Heroic's <c>installed.json</c> key, Lutris's slug.
/// Not always <see cref="GameLocatorResult.StoreIdentifier"/>: Epic games are identified by catalog item id there.</param>
/// <param name="IsFlatpak">The client runs from its Flatpak rather than a native install.</param>
[PublicAPI]
public sealed record StoreClient(StoreClientKind Kind, string AppName, bool IsFlatpak);

/// <summary>
/// A store client the app can hand a launch to.
/// </summary>
[PublicAPI]
public enum StoreClientKind
{
    Steam,
    Heroic,
    Lutris,
}
