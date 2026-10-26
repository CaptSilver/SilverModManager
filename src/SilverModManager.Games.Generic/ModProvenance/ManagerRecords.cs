using System.Collections.Immutable;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>
/// What other mod managers on this machine recorded about the mods they installed for a game, read-only. Mod
/// Organizer 2 and Fluorine give exact Nexus Mods ids, r2modman and Gale give Thunderstore packages. Vortex keeps its
/// state in a database this does not read; only its downloads folder is offered, and the result says so.
/// </summary>
public static class ManagerRecords
{
    public static ManagerRecordResult Read(ManagerSearch search, GameIdentity game)
    {
        var claims = ImmutableArray.CreateBuilder<ModClaim>();
        var archiveFolders = ImmutableArray.CreateBuilder<string>();
        var problems = ImmutableArray.CreateBuilder<string>();

        var installs = ManagerDiscovery.Find(search);
        foreach (var ini in installs.Mo2Inis) ReadInstance(ini, game, claims, archiveFolders, problems);
        foreach (var (root, origin) in installs.ThunderstoreRoots) ThunderstoreProfiles.Read(root, origin, game, claims, problems);

        foreach (var downloads in installs.VortexDownloads)
        {
            archiveFolders.Add(downloads);
            problems.Add($"Vortex was found ({downloads}). Only its downloads folder is read, not which mods it installed, so mods it installed are identified by their archives or names.");
        }

        return new ManagerRecordResult(claims.ToImmutable(), [..archiveFolders.Distinct()], problems.ToImmutable());
    }

    private static void ReadInstance(string iniPath, GameIdentity game, ImmutableArray<ModClaim>.Builder claims, ImmutableArray<string>.Builder archiveFolders, ImmutableArray<string>.Builder problems)
    {
        IniText ini;
        try
        {
            if (new FileInfo(iniPath).Length > Mo2Records.MaxIniBytes)
            {
                problems.Add($"{iniPath} is too large to be a ModOrganizer.ini and was not read.");
                return;
            }

            ini = IniText.Parse(File.ReadAllText(iniPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{iniPath} could not be read: {e.Message}");
            return;
        }

        var folder = Path.GetDirectoryName(iniPath) ?? "";
        if (!Mo2Records.IsForGame(ini, folder, game)) return;

        var (mods, downloads) = Mo2Records.Folders(ini, folder);
        archiveFolders.Add(downloads);
        Mo2Records.ReadMods(mods, Mo2Records.OriginOf(iniPath), claims, problems);
    }
}
