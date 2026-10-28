using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.HttpDownloader;

/// <summary>
/// A proxy for the app's web traffic, for networks that drop the CDN's address ranges and a VPN container that doesn't.
/// </summary>
public record HttpProxySettings : ISettings
{
    /// <summary>
    /// A proxy URL such as `http://127.0.0.1:8888`; empty uses the system's proxy settings.
    /// </summary>
    public string HttpProxy { get; init; } = "";

    /// <summary>
    /// .NET on Linux reads the proxy environment variables, not the desktop's proxy settings, so the text says that.
    /// </summary>
    public const string Description = "Send the app's web traffic through this proxy, for example http://127.0.0.1:8888 for a VPN container. Leave empty to use the proxy environment variables (http_proxy, https_proxy, all_proxy), if any are set.";

    public static string SettingsId => "SilverModManager.Networking.HttpDownloader.HttpProxySettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.HttpProxy,
            new PropertyOptions<HttpProxySettings, string>
            {
                Section = Sections.Advanced,
                DisplayName = "HTTP proxy",
                DescriptionFactory = _ => Description,
                RequiresRestart = true,
            },
            new TextContainerOptions()
        );
    }
}

public static class HttpProxy
{
    private static readonly string[] AllowedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, "socks5"];

    /// <summary>
    /// The proxy a setting value names. Empty means none and is not an error; anything else that isn't an
    /// http, https or socks5 URL is logged and ignored so the app still starts with the default proxy.
    /// </summary>
    public static Uri? Parse(string? value, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme))
        {
            // WebProxy would drop the credentials silently, and the value would end up in logs and the settings file.
            if (uri.UserInfo.Length > 0)
            {
                logger.LogWarning("Ignoring the HTTP proxy setting: it contains a user name and password, which the proxy setting doesn't support");
                return null;
            }

            return uri;
        }

        logger.LogWarning("Ignoring the HTTP proxy setting `{Value}`: it must be an http://, https:// or socks5:// URL", trimmed);
        return null;
    }

    /// <summary>
    /// Makes the configured proxy the default for every HttpClient handler. Call once at start-up, before any request.
    /// </summary>
    public static void Apply(HttpProxySettings settings, ILogger logger)
    {
        var uri = Parse(settings.HttpProxy, logger);
        if (uri is null) return;

        // Local addresses skip the proxy: a VPN container's proxy can't reach this machine's loopback.
        HttpClient.DefaultProxy = new System.Net.WebProxy(uri, BypassOnLocal: true);
        logger.LogInformation("Sending web traffic through the proxy at {Proxy}", uri.GetLeftPart(UriPartial.Authority));
    }
}
