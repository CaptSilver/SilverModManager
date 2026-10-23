using SilverModManager.Games.AdvancedInstaller.Exceptions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.AdvancedInstaller;

/// <summary>
/// Helpers for throwing methods with high performance.
/// </summary>
internal class ThrowHelpers
{
    public static void MappingAlreadyExists(GamePath outputPath, RelativePath existingPath, RelativePath attemptedPath)
    {
        throw new MappingAlreadyExistsException(outputPath, existingPath, attemptedPath);
    }
}
