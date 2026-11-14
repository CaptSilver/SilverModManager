namespace SilverModManager.Networking.ModIo;

/// <summary>
/// mod.io's REST API (<c>https://api.mod.io/v1</c>) with the user's own access token from the secret store. Every
/// request needs the token; without one the call throws <see cref="ModIoNotSignedInException"/> before anything is sent.
/// </summary>
public interface IModIoApi
{
    /// <summary>One page of a game's mods, each with its latest file.</summary>
    Task<ModIoPage<ModIoMod>> Mods(uint gameId, ModIoQuery query, CancellationToken ct);

    /// <summary>One mod, or null when the game has no such mod.</summary>
    Task<ModIoMod?> Mod(uint gameId, uint modId, CancellationToken ct);

    /// <summary>Every file the mod has uploaded, read page by page.</summary>
    Task<IReadOnlyList<ModIoFile>> Files(uint gameId, uint modId, CancellationToken ct);

    /// <summary>
    /// Subscribes the user to the mod, with the mods it depends on when asked. Subscribing again is not an error.
    /// </summary>
    /// <exception cref="ModIoRefusedException">The token was created without the write scope.</exception>
    Task Subscribe(uint gameId, uint modId, bool includeDependencies, CancellationToken ct);

    /// <summary>Removes the user's subscription.</summary>
    Task Unsubscribe(uint gameId, uint modId, CancellationToken ct);

    /// <summary>Every mod of the game the user is subscribed to, read page by page.</summary>
    Task<IReadOnlyList<ModIoMod>> Subscribed(uint gameId, CancellationToken ct);

    /// <summary>The user the stored token belongs to.</summary>
    Task<ModIoUser> Me(CancellationToken ct);

    /// <summary>
    /// The user a token not yet stored belongs to, which is how a pasted token is checked before it is kept.
    /// </summary>
    Task<ModIoUser> Me(string token, CancellationToken ct);
}
