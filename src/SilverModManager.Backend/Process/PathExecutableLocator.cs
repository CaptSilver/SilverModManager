using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Backend.Process;

/// <summary>Looks a program up on <c>PATH</c>. The variable is injectable so a test needs no process-wide change.</summary>
internal sealed class PathExecutableLocator(IFileSystem fileSystem, Func<string?>? pathVariable = null) : IExecutableLocator
{
    public AbsolutePath? Find(string name)
    {
        var path = pathVariable is null ? Environment.GetEnvironmentVariable("PATH") : pathVariable();
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var folder in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathRooted(folder)) continue;

            var candidate = Path.Combine(folder, name);
            if (!File.Exists(candidate) || !File.GetUnixFileMode(candidate).HasFlag(UnixFileMode.UserExecute)) continue;
            return fileSystem.FromUnsanitizedFullPath(candidate);
        }

        return null;
    }
}
