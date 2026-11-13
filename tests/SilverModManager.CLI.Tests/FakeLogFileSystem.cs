using NexusMods.Paths;
using SilverModManager.Abstractions.Cli;
using SilverModManager.CrossPlatform;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// A file system whose state folder is a made-up path, so a test can tell a crash pointer resolved on the injected
/// file system from one resolved on the real environment.
/// </summary>
internal static class FakeLogFileSystem
{
    public const string StateFolder = "/fake/state";

    public static IFileSystem Create()
    {
        var inMemory = new InMemoryFileSystem(OSInformation.FakeUnix);
        return inMemory.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_STATE_HOME] = inMemory.FromUnsanitizedFullPath(StateFolder) });
    }

    /// <summary>The pointer a crash prints when it is resolved on <see cref="Create"/>'s file system.</summary>
    public static string Pointer(IFileSystem fileSystem) => CliErrors.CrashPointerFor(fileSystem);
}
