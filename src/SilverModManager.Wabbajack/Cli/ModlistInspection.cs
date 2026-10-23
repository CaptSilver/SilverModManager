using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Cli;

/// <summary>What <c>wabbajack inspect</c> prints about a list, kept apart from the verb so it can be tested without a renderer.</summary>
internal static class ModlistInspection
{
    /// <summary>Directive type names with how many of each, in the order each first appears.</summary>
    public static IReadOnlyList<(string Kind, int Count)> DirectiveKinds(ModList list)
        => Count(list.Directives.Select(d => d.GetType().Name));

    /// <summary>Download source names with how many archives use each, in the order each first appears.</summary>
    public static IReadOnlyList<(string Kind, int Count)> SourceKinds(ModList list)
        => Count(list.Archives.Select(a => SourceName(a.State)));

    public static string SourceName(DownloadState state)
    {
        var name = state.GetType().Name;
        return name.EndsWith("State", StringComparison.Ordinal) ? name[..^"State".Length] : name;
    }

    /// <summary>The Nexus domain with mod and file ids, or the address, or whatever else names where the archive comes from.</summary>
    public static string SourceDetail(DownloadState state) => state switch
    {
        NexusState nexus => $"{nexus.GameName} {nexus.ModId}/{nexus.FileId}",
        HttpState http => http.Url.ToString(),
        WabbajackCdnState cdn => cdn.Url.ToString(),
        GameFileSourceState game => $"{game.Game} {game.GameFile} (game version {game.GameVersion})",
        ManualState manual => manual.Url.ToString(),
        MegaState mega => mega.Url.ToString(),
        GoogleDriveState drive => drive.Id,
        MediaFireState mediaFire => mediaFire.Url.ToString(),
        ModDbState modDb => modDb.Url.ToString(),
        LoversLabState loversLab => loversLab.Url?.ToString() ?? loversLab.FileName ?? "",
        VectorPlexusState vectorPlexus => vectorPlexus.Url?.ToString() ?? vectorPlexus.FileName ?? "",
        UnsupportedState unsupported => unsupported.Url ?? unsupported.TypeName,
        _ => state.GetType().Name,
    };

    public static object[] ArchiveRow(ArchiveEntry archive)
        => [archive.Name, archive.Size, SourceName(archive.State), SourceDetail(archive.State)];

    private static IReadOnlyList<(string Kind, int Count)> Count(IEnumerable<string> names)
        => names.GroupBy(n => n).Select(g => (g.Key, g.Count())).ToArray();
}
