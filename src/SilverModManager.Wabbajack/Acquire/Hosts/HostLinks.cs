namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Which addresses a landing page may send a job to. A page is data from the network, so a link on it is followed
/// only when it is https and on the host's own domain.
/// </summary>
internal static class HostLinks
{
    /// <summary>The address <paramref name="href"/> names relative to <paramref name="page"/>, if it is https and on <paramref name="domain"/> or a subdomain of it.</summary>
    public static Uri? Resolve(Uri page, string? href, string domain)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(page, href.Trim(), out var link)) return null;
        return IsOn(link, domain) ? link : null;
    }

    public static bool IsOn(Uri link, string domain)
        => link.Scheme == Uri.UriSchemeHttps && (link.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || link.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static HttpRequestMessage Get(Uri url, Uri? referer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) request.Headers.Referrer = referer;
        return request;
    }
}
