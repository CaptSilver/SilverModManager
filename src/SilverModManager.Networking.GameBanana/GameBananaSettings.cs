namespace SilverModManager.Networking.GameBanana;

/// <summary>
/// How the client treats the site. There is no published rate limit, so the client keeps to a modest one.
/// </summary>
/// <param name="RefreshAge">How long a listing page is kept before the site is asked again.</param>
public sealed record GameBananaSettings(TimeSpan RefreshAge)
{
    /// <summary>One hour, the age the shelf and the Thunderstore index are kept for.</summary>
    public static GameBananaSettings Default { get; } = new(TimeSpan.FromHours(1));
}
