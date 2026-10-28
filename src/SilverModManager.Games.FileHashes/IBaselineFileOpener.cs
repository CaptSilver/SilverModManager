using NexusMods.Paths;

namespace SilverModManager.Games.FileHashes;

/// <summary>
/// How <see cref="LocalVanillaBaseline"/> opens a game file to hash it. A baseline of a store-vouched install opens
/// none, so a test replaces this to count the opens.
/// </summary>
internal interface IBaselineFileOpener
{
    Stream OpenRead(AbsolutePath path);
}
