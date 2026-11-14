using System.Text;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Steam.Shortcuts;

/// <summary>
/// Asks the running Steam to add the shortcut, the way Bazzite's own "Add to Steam" does: touch a marker file,
/// then open <c>steam://addnonsteamgame/&lt;path&gt;</c>. Steam names the entry after the file and gives it no icon;
/// a later pass with Steam closed sets the name, icon and art.
/// </summary>
/// <param name="appIdsIn">The app ids in the <c>shortcuts.vdf</c> at the given path; empty when it is missing.</param>
/// <param name="markerFile">The file Steam looks for before it honours the URL.</param>
public sealed class SteamUrlShortcutAdder(ISteamUriLauncher steam, Func<string, IReadOnlySet<uint>> appIdsIn, AbsolutePath markerFile) : ISteamShortcutRoute
{
    public string Name => "steam-url";

    public Task<SteamShortcutWrite> AddAsync(SteamShortcutRequest request, CancellationToken token)
    {
        var shortcuts = ShortcutsVdfWriter.ShortcutsFilePath(request.SteamRoot, request.Account.AccountId);
        var before = appIdsIn(shortcuts);

        TouchMarker();
        steam.Open(new Uri("steam://addnonsteamgame/" + Encode(request.ShimPath.ToString())));

        // Steam may write the file at once or only when it exits; one look now, and the id stays unknown if it is not there.
        var added = appIdsIn(shortcuts).Except(before).Take(2).ToList();
        var write = added.Count == 1
            ? new SteamShortcutWrite(added[0], Confirmed: true, [])
            : new SteamShortcutWrite(null, Confirmed: false, []);
        return Task.FromResult(write);
    }

    // Steam fixes the marker's path in /tmp, where any user can leave a link or a file under that name first, so the
    // marker is never opened through what is there: a link is refused, a leftover file is removed, and the new one is
    // created exclusively, which fails rather than follows a link that appears in between.
    private void TouchMarker()
    {
        var path = markerFile.ToString();
        if (new FileInfo(path).LinkTarget is { } target)
            throw new SteamShortcutRefusedException($"Steam's marker file {MarkdownText.Code(path)} is a link to {MarkdownText.Code(target)}, so the app did not touch it. Delete it if you did not make it, then try again.", removing: false);

        try
        {
            File.Delete(path);
            using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SteamShortcutRefusedException($"Could not make Steam's marker file {MarkdownText.Code(path)}: {MarkdownText.Inline(ex.Message)}", removing: false);
        }
    }

    /// <summary>
    /// Percent-encodes every byte outside <c>[A-Za-z0-9_.~-]</c>, so <c>/</c> becomes <c>%2F</c>.
    /// </summary>
    public static string Encode(string text)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '~' or '-')) builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
