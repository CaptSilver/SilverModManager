using SilverModManager.App.GarbageCollection.Errors;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.App.GarbageCollection.Utilities;

internal static class ThrowHelpers
{
    internal static void ThrowUnknownFileException(FileIdentity identity) => throw new UnknownFileException(identity);
}
