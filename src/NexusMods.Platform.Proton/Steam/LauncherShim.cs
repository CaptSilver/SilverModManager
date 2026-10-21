using NexusMods.Paths;

namespace NexusMods.Platform.Proton.Steam;

/// <summary>
/// The stable <c>silvermod</c> script that Steam launch options point at. The app's own path changes with
/// every AppImage update, so the launch line names the script and the script execs whatever the app was last
/// started as.
/// </summary>
/// <param name="dataHome">The XDG data directory; the script goes in <c>silvermod/bin</c> under it.</param>
public sealed class LauncherShim(AbsolutePath dataHome)
{
    private const string Name = "silvermod";
    private const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                      | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Where the script lives.
    /// </summary>
    public AbsolutePath ShimPath => dataHome.Combine(Name).Combine("bin").Combine(Name);

    /// <summary>
    /// Makes the script exec <paramref name="target"/>. Returns false, touching nothing, when the script
    /// already does that and is executable.
    /// </summary>
    public bool Write(AbsolutePath target)
    {
        var path = ShimPath.ToString();
        var content = ContentFor(target);

        if (File.Exists(path) && File.ReadAllText(path) == content && File.GetUnixFileMode(path) == Mode) return false;

        ShimPath.Parent.CreateDirectory();

        // A game may be launching through the old script right now, so swap it in whole instead of truncating.
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.SetUnixFileMode(temp, Mode);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>
    /// Whether the script exists and execs <paramref name="target"/>.
    /// </summary>
    public bool Targets(AbsolutePath target)
    {
        var path = ShimPath.ToString();
        return File.Exists(path) && File.ReadAllText(path) == ContentFor(target);
    }

    /// <summary>
    /// <c>$XDG_DATA_HOME</c> when it is an absolute path, otherwise <c>~/.local/share</c>. The XDG Base Directory
    /// Specification says a relative value must be ignored.
    /// </summary>
    public static AbsolutePath ResolveDataHome(IEnvironmentReader environment, IFileSystem fileSystem)
    {
        var xdg = environment.Get("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && xdg.StartsWith('/')) return fileSystem.FromUnsanitizedFullPath(xdg);

        var home = environment.Get("HOME");
        var homePath = !string.IsNullOrEmpty(home) && home.StartsWith('/')
            ? fileSystem.FromUnsanitizedFullPath(home)
            : fileSystem.GetKnownPath(KnownPath.HomeDirectory);
        return homePath.Combine(".local/share");
    }

    private static string ContentFor(AbsolutePath target) => $"#!/bin/sh\nexec {SteamLaunchOptionLine.ShellQuote(target.ToString())} \"$@\"\n";
}
