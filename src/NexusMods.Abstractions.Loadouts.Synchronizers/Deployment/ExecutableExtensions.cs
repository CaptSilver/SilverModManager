using NexusMods.Paths;

namespace NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// Which deployed files get the execute bit, and the one place that sets it.
/// </summary>
internal static class ExecutableExtensions
{
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Makes the file executable when its extension says it is a Linux-native program or script, and
    /// not executable otherwise.
    /// </summary>
    /// <remarks>
    /// The bits are set rather than added to. A copy starts without them, so the result is the same
    /// there, but a linked file shares its inode with the staged file and with whatever else was
    /// linked from it: a hash first deployed as <c>bin/launcher</c> and later as <c>README.txt</c> must not keep
    /// the bits it got the first time. The user execute bit is part of what the deployed-tree snapshot pins.
    /// </remarks>
    internal static void ApplyTo(AbsolutePath path)
    {
        var mode = path.GetUnixFileMode() & ~ExecuteBits;

        // Note (Sewer): I don't think we'd ever need anything other than just 'user' execute, but you can never
        // be sure. Just in case, I'll throw in group and other to match 'chmod +x' behaviour.
        if (IsExecutable(path.Extension.ToString().ToLower())) mode |= ExecuteBits;

        path.SetUnixFileMode(mode);
    }

    private static bool IsExecutable(string extension) => extension is "" or ".sh" or ".bin" or ".run" or ".py" or ".pl" or ".php" or ".rb" or ".out" or ".elf";
}
