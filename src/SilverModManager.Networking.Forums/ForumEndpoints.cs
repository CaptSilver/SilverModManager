using System.Text;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// Where a forum's OAuth server and files API are and how its client is set up. These are facts about the
/// server, so they live in code per site; only the client id can be overridden (<see cref="ForumAuthSettings"/>).
/// </summary>
/// <param name="ClientId">
/// The public client id the forum's admins issued. Empty until they do, which keeps sign-in switched off.
/// </param>
/// <param name="UsePkce">
/// Whether the authorize URL carries an S256 challenge and the token POST the verifier. IPS support for it is
/// unconfirmed; <c>state</c> is sent and checked either way.
/// </param>
public sealed record ForumEndpoints(
    string ClientId, Uri Authorize, Uri Token, Uri ApiBase,
    string Scope, bool UsePkce)
{
    // Both ids stay empty until an admin issues a client to this app. Wabbajack's ids belong to Wabbajack
    // and must not be used here.
    private static readonly ForumEndpoints LoversLab = new(
        ClientId: "",
        Authorize: new Uri("https://api.loverslab.com/oauth/authorize/"),
        Token: new Uri("https://api.loverslab.com/oauth/token/"),
        ApiBase: new Uri("https://api.loverslab.com/"),
        Scope: "downloads",
        UsePkce: true);

    // The host is unconfirmed: Wabbajack used vectorplexis.com, which is parked now, and the paths on
    // vectorplexus.com are unverified. Sign-in refuses without a client id, so a wrong host does nothing
    // until an admin has answered.
    private static readonly ForumEndpoints VectorPlexus = new(
        ClientId: "",
        Authorize: new Uri("https://vectorplexus.com/oauth/authorize/"),
        Token: new Uri("https://vectorplexus.com/oauth/token/"),
        ApiBase: new Uri("https://vectorplexus.com/"),
        Scope: "profile get_downloads",
        UsePkce: true);

    /// <summary>
    /// The built-in endpoints for a site.
    /// </summary>
    public static ForumEndpoints For(ForumSite site) => site switch
    {
        ForumSite.LoversLab => LoversLab,
        ForumSite.VectorPlexus => VectorPlexus,
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
    };

    /// <summary>
    /// The built-in endpoints for a site with the settings' client id in place of the built-in one when the
    /// settings hold one.
    /// </summary>
    public static ForumEndpoints For(ForumSite site, ForumAuthSettings settings)
    {
        var builtIn = For(site);
        var overrideId = site switch
        {
            ForumSite.LoversLab => settings.LoversLabClientId,
            ForumSite.VectorPlexus => settings.VectorPlexusClientId,
            _ => throw new ArgumentOutOfRangeException(nameof(site), site, null),
        };
        return string.IsNullOrWhiteSpace(overrideId) ? builtIn : builtIn with { ClientId = overrideId };
    }

    /// <summary>
    /// The page the user approves the sign-in on.
    /// </summary>
    /// <param name="redirect">Where the forum sends the code; always the loopback listener's address.</param>
    /// <param name="state">The anti-forgery value the listener checks on the way back.</param>
    /// <param name="challenge">The S256 code challenge; required when <see cref="UsePkce"/> is on, otherwise ignored.</param>
    public Uri BuildAuthorizeUri(Uri redirect, string state, string? challenge)
    {
        if (redirect.Scheme != Uri.UriSchemeHttp || redirect.Host != "127.0.0.1")
            throw new ArgumentException("The sign-in redirect must be a http://127.0.0.1 loopback address.", nameof(redirect));
        if (UsePkce && string.IsNullOrEmpty(challenge))
            throw new ArgumentException("A code challenge is required while PKCE is on.", nameof(challenge));
        var pkceChallenge = UsePkce ? challenge : null;

        var query = new List<(string Name, string Value)>
        {
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", redirect.AbsoluteUri),
            ("scope", Scope),
            ("state", state),
        };
        if (pkceChallenge is not null)
        {
            query.Add(("code_challenge", pkceChallenge));
            query.Add(("code_challenge_method", "S256"));
        }

        var builder = new StringBuilder(Authorize.GetLeftPart(UriPartial.Path));
        for (var i = 0; i < query.Count; i++)
            builder.Append(i == 0 ? '?' : '&').Append(query[i].Name).Append('=').Append(Uri.EscapeDataString(query[i].Value));
        return new Uri(builder.ToString());
    }
}
