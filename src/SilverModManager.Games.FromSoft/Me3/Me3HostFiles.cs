using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>
/// The files of the me3 release that are kept in <see cref="SilverModManager.Sdk.Games.LocationId.Me3Host"/>, and the
/// executable bit the binary needs. Deploying by copy or link does not carry a file mode, so the binary arrives
/// without it.
/// </summary>
public static class Me3HostFiles
{
    public const string Binary = "bin/me3";
    public const string Launcher = "bin/win64/me3-launcher.exe";
    public const string ModHost = "bin/win64/me3_mod_host.dll";

    /// <summary>The GitHub repository id of me3, which survives a rename or a transfer of the repository.</summary>
    public const ulong RepositoryId = 540883721;

    /// <summary>
    /// The end of the Linux release's asset name. The Windows zip and the debug builds do not end with it, so asking
    /// for this suffix can never return one of them.
    /// </summary>
    public const string ReleaseAssetSuffix = "linux-amd64.tar.gz";

    /// <summary>Preferred part of the asset name when a release carries more than one Linux tarball.</summary>
    public const string ReleaseAssetHint = "me3-linux-amd64";

    /// <summary>Everything me3 reads at run time. The binary resolves the other two out of its own folder tree.</summary>
    public static readonly string[] ReleaseFiles = [Binary, Launcher, ModHost];

    private const UnixFileMode Mode755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static bool IsExecutable(AbsolutePath binary)
        => File.Exists(binary.ToString()) && File.GetUnixFileMode(binary.ToString()).HasFlag(UnixFileMode.UserExecute);

    /// <summary>Sets mode 755 on the binary. Throws <see cref="FileNotFoundException"/> when it is not there.</summary>
    public static void EnsureExecutable(AbsolutePath binary)
    {
        if (!File.Exists(binary.ToString())) throw new FileNotFoundException("The me3 binary is not installed.", binary.ToString());
        File.SetUnixFileMode(binary.ToString(), Mode755);
    }
}
