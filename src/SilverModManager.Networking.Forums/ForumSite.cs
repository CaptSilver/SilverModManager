using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// The Invision Power Suite forums whose downloads sit behind a sign-in. The two share code, not state:
/// each has its own client id, token and secret store entry.
/// </summary>
public enum ForumSite
{
    LoversLab,
    VectorPlexus,
}

public static class ForumSiteExtensions
{
    public static string DisplayName(this ForumSite site) => site switch
    {
        ForumSite.LoversLab => "LoversLab",
        ForumSite.VectorPlexus => "VectorPlexus",
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
    };

    public static SecretKey SecretKey(this ForumSite site) => site switch
    {
        ForumSite.LoversLab => ForumSecretKeys.LoversLab,
        ForumSite.VectorPlexus => ForumSecretKeys.VectorPlexus,
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
    };
}
