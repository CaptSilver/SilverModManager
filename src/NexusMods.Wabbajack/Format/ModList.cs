using NexusMods.Hashing.xxHash3;

namespace NexusMods.Wabbajack.Format;

/// <summary>A parsed <c>modlist</c> file. <paramref name="GameType"/> is Wabbajack's game enum name.</summary>
public sealed record ModList(
    string Name,
    string Author,
    string Description,
    string Version,
    string? WabbajackVersion,
    string GameType,
    string[] OtherGames,
    string Image,
    string Readme,
    string Website,
    string Community,
    bool IsNsfw,
    IReadOnlyList<ArchiveEntry> Archives,
    IReadOnlyList<Directive> Directives);

/// <summary>One download the list needs. <c>Meta</c> is the ini text Wabbajack keeps beside it, unused here.</summary>
public sealed record ArchiveEntry(Hash XxHash64, long Size, string Name, string Meta, DownloadState State);
