namespace SilverModManager.Networking.Steam.Workshop;

/// <summary>
/// How long what Steam said about the Workshop items of a game is kept before Steam is asked again.
/// </summary>
/// <param name="RefreshAge">The age at which an answer is asked for again.</param>
public sealed record SteamWorkshopSettings(TimeSpan RefreshAge)
{
    /// <summary>One hour, the age the other sources keep their listings for.</summary>
    public static SteamWorkshopSettings Default { get; } = new(TimeSpan.FromHours(1));
}
