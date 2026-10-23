namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// The archive-level values a rebuilt archive has to repeat to be accepted by the game.
/// </summary>
public abstract record ArchiveState;

/// <summary>
/// Morrowind. <paramref name="DataOffset"/> is where the file data block starts; the other fields are the
/// header values.
/// </summary>
public sealed record Tes3ArchiveState(uint FileCount, long DataOffset, uint HashOffset, uint VersionNumber) : ArchiveState;

/// <summary>
/// Oblivion through Skyrim SE. <paramref name="Version"/> is 103, 104 or 105.
/// </summary>
public sealed record Tes5ArchiveState(string Magic, uint Version, uint ArchiveFlags, uint FileFlags) : ArchiveState;
