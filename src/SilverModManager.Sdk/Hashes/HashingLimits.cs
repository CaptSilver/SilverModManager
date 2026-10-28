namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Limits shared by every loop that streams whole game files through a hasher.
/// </summary>
public static class HashingLimits
{
    /// <summary>
    /// How many files are read at once. A loop with no cap starts one read per CPU thread, which on a large
    /// modded game saturates the disk and starves the rest of the app.
    /// </summary>
    public const int FileReaders = 4;
}
