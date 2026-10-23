using SilverModManager.Networking.ModUpdates;

namespace SilverModManager.Networking.ModUpdates.Tests.Helpers;

/// <summary>
/// A file as listed on a Nexus mod page, with its page category.
/// </summary>
public record RecordedFile(ulong FileId, string Version, string Category, string FileName)
{
    public PinFile ToPinFile() => new(FileId, FileName, Version);
}

/// <summary>
/// Real Nexus file lists. They are kept as data rather than fetched because they are the shapes that make
/// variant matching hard: nine sibling variants, same-version pairs and a page version that disagrees
/// with its own file names.
/// </summary>
public static class RecordedNexusFileLists
{
    public const ulong MapForGoblinsModId = 10062;
    public const ulong NoFallDeadModId = 10402;
    public const ulong BossResurrectionModId = 2790;
    public const ulong ForeverBuffsModId = 8644;
    public const ulong CleversModId = 1928;
    public const ulong BetterBowsModId = 4628;

    public static readonly RecordedFile[] MapForGoblins =
    [
        new(48311, "v2.0.5", "OLD_VERSION", "Vanilla or Randomizer - v2.0.5 10062 v2.0.5 2026-07-14T10-47Z ZTHtIFwXR.zip"),
        new(48931, "v2.1.2", "MAIN", "Graceborne - v2.1.2 10062 v2.1.2 2026-08-06T08-51Z ZTHtIFfdq.zip"),
        new(48932, "v2.1.2", "MAIN", "Elden Vins - v2.1.2 10062 v2.1.2 2026-08-06T08-52Z SitYwbVNx.zip"),
        new(48933, "v2.1.2", "MAIN", "Reborn - v2.1.2 10062 v2.1.2 2026-08-06T08-52Z Ls9XqlxeQ.zip"),
        new(48934, "v2.1.2", "MAIN", "GoldenAge (v3.6.1) - v2.1.2 10062 v2.1.2 2026-08-06T08-52Z KeUMabcde.zip"),
        new(48935, "v2.1.2", "MAIN", "GoldenAge (v3.6.5) - v2.1.2 10062 v2.1.2 2026-08-06T08-52Z hIDyEfZXz.zip"),
        new(48936, "v2.1.2", "MAIN", "ERTE - v2.1.2 10062 v2.1.2 2026-08-06T08-53Z IDyEfZXzP.zip"),
        new(48937, "v2.1.2", "MAIN", "Convergence 3.x - v2.1.2 10062 v2.1.2 2026-08-06T08-53Z KeUMi2m9p.zip"),
        new(48938, "v2.1.2", "MAIN", "ERR - v2.1.2 10062 v2.1.2 2026-08-06T08-53Z i2m9p03hh.zip"),
        new(48939, "v2.1.2", "MAIN", "Vanilla or Randomizer - v2.1.2 10062 v2.1.2 2026-08-06T08-53Z ZTHtIFwXQ.zip"),
    ];

    public static readonly RecordedFile[] NoFallDead =
    [
        new(48339, "1", "MAIN", "NOFALLDEAD 10402 1 2026-07-15T03-58Z 2fNK8JduR.zip"),
        new(48340, "1", "MAIN", "NoFallDead Longtail Cat Version 10402 1 2026-07-15T04-00Z HqR4yjEgC.zip"),
    ];

    public static readonly RecordedFile[] BossResurrection =
    [
        new(24924, "2.0.1", "MAIN", "Boss Resurrection-2790-2-0-1-1720450828.zip"),
        new(24925, "2.0.1", "MAIN", "Boss Resurrection - Lite-2790-2-0-1-1720450846.zip"),
    ];

    public static readonly RecordedFile[] ForeverBuffs =
    [
        new(39767, "v1.0", "MAIN", "CSV Included to merge-8644-v1-0-1756694897.rar"),
        new(39807, "v1.0", "MAIN", "Forever Buffs N all kinds of Buff included-8644-v1-0-1756805123.zip"),
    ];

    public static readonly RecordedFile[] Clevers =
    [
        new(34558, "25.0", "OLD_VERSION", "moveset_modpack 25.0-1928-25-0-1738532654.zip"),
        new(48590, "26.0", "OLD_VERSION", "moveset_modpack 26.0 1928 26.0 2026-07-23T18-55Z sVKWduz6C.zip"),
        new(49639, "26.1", "MAIN", "moveset_modpack 26.1 1928 26.1 2026-08-27T23-54Z xzSjGqMUM.zip"),
    ];

    // The page reads version 1.5, but the only MAIN is the 1.2 regulation. The 1.5 files are param
    // source spreadsheets, not installable.
    public static readonly RecordedFile[] BetterBows =
    [
        new(41004, "1.2", "MAIN", "Regulation.bin-4628-1-2-1760355506.7z"),
        new(41005, "1.5", "ARCHIVED", "CSV files-4628-1-5-1760355583.7z"),
        new(41410, "1.5", "OPTIONAL", "CSV files-4628-1-5-1761489294.7z"),
    ];

    public static RecordedFile ById(IEnumerable<RecordedFile> files, ulong fileId) => files.First(f => f.FileId == fileId);

    /// <summary>
    /// The replacements a page offers for a pinned file: its MAIN files other than the pin itself.
    /// </summary>
    public static PinFile[] MainsOtherThan(IEnumerable<RecordedFile> files, ulong pinnedFileId) => files
        .Where(f => f.Category == "MAIN" && f.FileId != pinnedFileId)
        .Select(f => f.ToPinFile())
        .ToArray();
}
