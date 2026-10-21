using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Me3;

/// <summary>
/// The files of the me3 release that are kept in <see cref="NexusMods.Sdk.Games.LocationId.Me3Host"/>, and the
/// executable bit the binary needs. Deploying by copy or link does not carry a file mode, so the binary arrives
/// without it.
/// </summary>
public static class Me3HostFiles
{
    public const string Binary = "bin/me3";
    public const string Launcher = "bin/win64/me3-launcher.exe";
    public const string ModHost = "bin/win64/me3_mod_host.dll";

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
