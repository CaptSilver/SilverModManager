using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>Finds a program the way a shell would, so a test can say where (or whether) it is.</summary>
public interface IExecutableLocator
{
    /// <summary>The first executable called <paramref name="name"/> on <c>PATH</c>, or null.</summary>
    AbsolutePath? Find(string name);
}

internal sealed class PathExecutableLocator(IFileSystem fileSystem) : IExecutableLocator
{
    public AbsolutePath? Find(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
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
