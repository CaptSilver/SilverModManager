using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// What a route needs to put the app into one account's library.
/// </summary>
/// <param name="Match">Which existing entry counts as ours, when the route replaces one.</param>
public sealed record SteamShortcutRequest(SteamAccount Account, AbsolutePath SteamRoot, AbsolutePath ShimPath, AbsolutePath IconPath, ShortcutMatch? Match = null);

/// <param name="AppId">The id Steam stored, or null when it is not known yet.</param>
/// <param name="Confirmed">Whether the entry was seen in <c>shortcuts.vdf</c>.</param>
/// <param name="FilesWritten">Files this app wrote for the entry, for removal later.</param>
/// <param name="Warning">
/// Something the user must be told although the entry was written (art that could not be saved, Steam starting
/// mid-write). The caller records the write first and then reports it.
/// </param>
public sealed record SteamShortcutWrite(uint? AppId, bool Confirmed, IReadOnlyList<AbsolutePath> FilesWritten, string? Warning = null);

/// <summary>
/// One way of getting the app into Steam's library. A route that cannot do it throws a
/// <see cref="SilverModManager.Sdk.Errors.UserFacingException"/>.
/// </summary>
public interface ISteamShortcutRoute
{
    string Name { get; }

    Task<SteamShortcutWrite> AddAsync(SteamShortcutRequest request, CancellationToken token);
}
