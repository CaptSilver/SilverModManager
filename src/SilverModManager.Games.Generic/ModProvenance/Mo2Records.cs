using System.Collections.Immutable;
using System.Globalization;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>
/// Reads a Mod Organizer 2 instance (also Fluorine, an MO2 fork): <c>ModOrganizer.ini</c> says which game the instance
/// is for and where its mods and downloads are, and each <c>mods/&lt;name&gt;/meta.ini</c> says which Nexus Mods page and
/// file the mod came from. Everything is read-only and every file is untrusted.
/// </summary>
internal static class Mo2Records
{
    internal const long MaxIniBytes = 2_000_000;
    private const long MaxMetaBytes = 262_144;
    private const int MaxMods = 20_000;

    public static string OriginOf(string iniPath)
        => iniPath.Contains("fluorine", StringComparison.OrdinalIgnoreCase) ? "Fluorine" : "Mod Organizer 2";

    /// <summary>Whether the instance at <paramref name="ini"/> is for <paramref name="game"/>. A game path that can be placed decides; otherwise the game name does.</summary>
    public static bool IsForGame(IniText ini, string instanceFolder, GameIdentity game)
    {
        if (HostPaths.FromWindows(ini.Get("General", "gamePath"), instanceFolder) is { } path) return HostPaths.IsGameFolderOrItsParent(path, game.GameFolder);

        var name = Names.Squash(ini.Get("General", "gameName"));
        return name.Length > 0 && game.Names.Any(n => Names.Squash(n) == name);
    }

    public static (string Mods, string Downloads) Folders(IniText ini, string instanceFolder)
    {
        var baseDirectory = HostPaths.FromWindows(ini.Get("Settings", "base_directory"), instanceFolder) ?? instanceFolder;

        string Resolve(string key, string fallback)
        {
            var value = ini.Get("Settings", key)?.Replace("%BASE_DIR%", baseDirectory, StringComparison.OrdinalIgnoreCase);
            return HostPaths.FromWindows(value, baseDirectory) ?? Path.Combine(baseDirectory, fallback);
        }

        return (Resolve("mod_directory", "mods"), Resolve("download_directory", "downloads"));
    }

    public static void ReadMods(string modsFolder, string origin, ImmutableArray<ModClaim>.Builder claims, ImmutableArray<string>.Builder problems)
    {
        if (!Directory.Exists(modsFolder)) return;

        var count = 0;
        foreach (var mod in ManagerDiscovery.Children(modsFolder))
        {
            if (++count > MaxMods)
            {
                problems.Add($"{modsFolder} holds more than {MaxMods} mods; the rest were not read.");
                break;
            }

            var meta = Path.Combine(mod, "meta.ini");
            if (!File.Exists(meta)) continue;

            try
            {
                if (new FileInfo(meta).Length > MaxMetaBytes)
                {
                    problems.Add($"{meta} is too large to be a mod's meta.ini and was not read.");
                    continue;
                }

                if (Claim(Path.GetFileName(mod), origin, IniText.Parse(File.ReadAllText(meta))) is { } claim) claims.Add(claim);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{meta} could not be read: {e.Message}");
            }
        }
    }

    private static ModClaim? Claim(string mod, string origin, IniText meta)
    {
        // Only Nexus Mods installs carry ids we can use; a mod from anywhere else has none worth claiming.
        if (meta.Get("General", "repository") is { Length: > 0 } repository && !repository.Equals("Nexus", StringComparison.OrdinalIgnoreCase)) return null;

        var modId = Id(meta.Get("General", "modid"));
        uint? fileId = null;
        foreach (var (key, value) in meta.Section("installedFiles").OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!key.EndsWith("\\fileid", StringComparison.OrdinalIgnoreCase) || Id(value) is not { } id) continue;

            fileId = id;
            modId ??= meta.Section("installedFiles").TryGetValue(key[..^"fileid".Length] + "modid", out var m) ? Id(m) : null;
            break;
        }

        if (modId is null && fileId is null) return null;

        var archive = meta.Get("General", "installationFile");
        return new ModClaim(mod, ClaimMethod.ManagerRecords, origin, ClaimSource.Nexus, modId, fileId,
            meta.Get("General", "version") is { Length: > 0 } version ? version : null,
            string.IsNullOrWhiteSpace(archive) ? null : archive);
    }

    private static uint? Id(string? text)
        => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;
}
