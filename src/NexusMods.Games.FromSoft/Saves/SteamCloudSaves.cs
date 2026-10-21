using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Saves;

/// <summary>A save file Steam Cloud holds for one account, as Steam's local cache of the remote files lists it.</summary>
/// <param name="AccountId">The Steam account's folder name under <c>userdata</c>.</param>
/// <param name="SteamId64">The id the game's save folder is named by.</param>
/// <param name="RelativePath">The remote path, <c>EldenRing/&lt;steam id&gt;/&lt;file&gt;</c>.</param>
public sealed record CloudSave(string AccountId, string SteamId64, string RelativePath, long Size)
{
    public string FileName => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>What to do in Steam so the remote copy does not come back after the local save was moved away.</summary>
    public string PurgeInstruction =>
        $"Steam Cloud account {AccountId}: in Steam, turn Steam Cloud off for Elden Ring, then delete the remote {RelativePath} " +
        "(Settings > Cloud > Manage), or Steam will put it back.";
}

/// <summary>
/// Reads which Elden Ring saves Steam Cloud holds. Only reads: Steam's files are never written.
/// </summary>
public static partial class SteamCloudSaves
{
    // One entry per remote file: its quoted path, then a block holding the size. Steam's cache is a VDF text file;
    // the path is all that is needed from it, so a regex does, the way the tool this was ported from reads it.
    [GeneratedRegex("\"(EldenRing/(\\d+)/[^\"]+)\"\\s*\\{[^}]*?\"size\"\\s*\"(\\d+)\"", RegexOptions.Singleline)]
    private static partial Regex EntryPattern();

    /// <summary>
    /// The cloud saves of every account under <c>userdata</c> in the Steam folder. An account whose cache cannot be
    /// read is skipped with a warning and does not stop the others, since the Steam folder may sit on a drive
    /// that is not mounted.
    /// </summary>
    public static IReadOnlyList<CloudSave> Discover(AbsolutePath steamRoot, ILogger? logger = null)
    {
        var found = new List<CloudSave>();
        var userdata = steamRoot.GetFullPath() + "/userdata";

        IEnumerable<string> accounts;
        try
        {
            if (!Directory.Exists(userdata)) return found;
            accounts = Directory.EnumerateDirectories(userdata).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Could not list Steam's userdata folder {Folder}", userdata);
            return found;
        }

        foreach (var account in accounts)
        {
            var cache = Path.Combine(account, EldenRingPaths.SteamAppId.ToString(), "remotecache.vdf");
            string text;
            try
            {
                if (!File.Exists(cache)) continue;
                text = File.ReadAllText(cache);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(exception, "Could not read Steam Cloud's file list {File}", cache);
                continue;
            }

            foreach (Match match in EntryPattern().Matches(text))
                found.Add(new CloudSave(Path.GetFileName(account), match.Groups[2].Value, match.Groups[1].Value, long.Parse(match.Groups[3].Value)));
        }

        return found;
    }
}
