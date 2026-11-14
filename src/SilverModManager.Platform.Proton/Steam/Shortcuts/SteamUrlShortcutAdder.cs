using System.Text;
using NexusMods.Paths;
using SilverModManager.Sdk;

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

        File.Create(markerFile.ToString()).Dispose();
        steam.Open(new Uri("steam://addnonsteamgame/" + Encode(request.ShimPath.ToString())));

        // Steam may write the file at once or only when it exits; one look now, and the id stays unknown if it is not there.
        var added = appIdsIn(shortcuts).Except(before).Take(2).ToList();
        var write = added.Count == 1
            ? new SteamShortcutWrite(added[0], Confirmed: true, [])
            : new SteamShortcutWrite(null, Confirmed: false, []);
        return Task.FromResult(write);
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
