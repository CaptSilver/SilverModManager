namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

public enum SteamShortcutState
{
    NotInSteam,
    InSteam,

    /// <summary>Steam was asked to add it and the entry has not been seen in the file yet.</summary>
    AddedUnconfirmed,

    /// <summary>More than one account could own the shortcut; <see cref="SteamShortcutStatus.Candidates"/> lists them.</summary>
    AccountUndecided,

    /// <summary>No native Steam install, or a Flatpak one the launcher script cannot be reached from.</summary>
    NoSteam,
}

public sealed record SteamShortcutStatus(SteamShortcutState State, SteamAccount? Account, uint? AppId, IReadOnlyList<SteamAccount> Candidates);

/// <summary>
/// The one thing the settings row, its dialog and the command line call to add the app to Steam's library, remove
/// it, or ask where it stands. Refusals are <see cref="SteamShortcutRefusedException"/>.
/// </summary>
public interface ISteamShortcutService
{
    /// <exception cref="SteamShortcutRefusedException">The file cannot be read.</exception>
    SteamShortcutStatus Status(uint? accountId = null);

    /// <param name="route">The <see cref="ISteamShortcutRoute.Name"/>: <c>file</c> with Steam closed, <c>steam-url</c> with it running.</param>
    Task AddAsync(string route, uint? accountId, CancellationToken token);

    Task RemoveAsync(uint? accountId, CancellationToken token);
}
