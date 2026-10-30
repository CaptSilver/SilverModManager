namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Which of a modlist's request headers the app will send. A list is written by someone else, so a header that could
/// carry a credential (cookies, authorization, API keys) or redirect the request (Host) is never sent: the archive is
/// left for the user to download in a browser where they are signed in.
/// </summary>
public static class HeaderPolicy
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Cache-Control", "Origin", "Pragma", "Referer", "User-Agent",
    };

    /// <summary>Splits <c>Name: value</c> on the first colon.</summary>
    public static bool TryParse(string line, out string name, out string value)
    {
        name = value = "";
        var colon = line.IndexOf(':');
        if (colon <= 0) return false;
        name = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return name.Length > 0;
    }

    public static bool IsAllowed(string name) => Allowed.Contains(name);

    /// <summary>True when every header parses and is on the allowed list; a list with no headers qualifies.</summary>
    public static bool CanSendAll(IEnumerable<string> headers) => !Refused(headers).Any();

    /// <summary>The names of the headers that would not be sent; a line that is not a header is named as written.</summary>
    public static IEnumerable<string> Refused(IEnumerable<string> headers)
    {
        foreach (var line in headers)
        {
            if (!TryParse(line, out var name, out _)) yield return line.Trim();
            else if (!IsAllowed(name)) yield return name;
        }
    }
}
