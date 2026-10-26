using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk;

namespace SilverModManager.SingleProcess;

/// <summary>
/// Which build of SilverModManager a process is: its version and the commit it was built from.
/// </summary>
public sealed record AppBuild(string Version, string? CommitHash)
{
    /// <summary>
    /// The build this process is running.
    /// </summary>
    public static AppBuild Current { get; } = new(ApplicationConstants.Version.ToString(), ApplicationConstants.CommitHash);

    /// <summary>
    /// The identity a peer announced.
    /// </summary>
    public static AppBuild From(VersionHello hello) => new(hello.Version, hello.CommitHash);

    /// <summary>
    /// The greeting that announces this identity.
    /// </summary>
    public VersionHello ToHello() => new() { Version = Version, CommitHash = CommitHash };

    /// <summary>
    /// How to name this build to a person: the version, with the start of the commit when the other side has the
    /// same version, so two builds of one version are not described identically.
    /// </summary>
    public string Describe(AppBuild other) =>
        Version == other.Version && CommitHash is { Length: > 0 } commit ? $"{Version} ({Short(commit)})" : Version;

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}
