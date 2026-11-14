using CliWrap;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// Hands <c>steam://</c> URLs to the <c>steam</c> binary, which forwards them to the running client. With no
/// <c>steam</c> on <c>PATH</c> (Flatpak Steam, a bare session) the OS URL handler gets them instead.
/// </summary>
public sealed class SteamBinaryUriLauncher(IProcessRunner runner, IOSInterop interop, IEnvironmentReader environment, Func<string, bool> fileExists) : ISteamUriLauncher
{
    /// <inheritdoc />
    public void Open(Uri steamUri)
    {
        if (!string.Equals(steamUri.Scheme, "steam", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not a steam:// URL: {steamUri}", nameof(steamUri));

        if (FindSteam() is { } steam)
            runner.Run(Cli.Wrap(steam).WithArguments([steamUri.AbsoluteUri]));
        else
            interop.OpenUri(steamUri);
    }

    private string? FindSteam()
    {
        var path = environment.Get("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "steam"))
            .FirstOrDefault(fileExists);
    }
}
