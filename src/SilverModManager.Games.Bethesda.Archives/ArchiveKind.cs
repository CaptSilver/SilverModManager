namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The container family of an archive, decided by its magic bytes.
/// </summary>
public enum ArchiveKind
{
    /// <summary>Morrowind BSA, starts with <c>00 01 00 00</c>.</summary>
    Tes3,

    /// <summary>Oblivion, Fallout 3, New Vegas, Skyrim and Skyrim SE BSA, starts with <c>BSA\0</c>.</summary>
    Tes5,

    /// <summary>Fallout 4 and later BA2, starts with <c>BTDX</c>.</summary>
    Ba2,
}
