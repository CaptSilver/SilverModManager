using CliWrap;
using NexusMods.Paths;
using NexusMods.Sdk;

namespace NexusMods.Platform.Proton.Protontricks;

/// <summary>
///     Protontricks is a tool that helps manage Proton games and their dependencies.
///     This works with the native version of Protontricks.
/// </summary>
/// <remarks>
/// There is an implicit assumption made here that if `protontricks` is installed,
/// then `protontricks-launch` is also available. Should it not be available, then
/// it means that `protontricks` was incorrectly packaged by the package maintainer
/// for a specific repo.
/// </remarks>
public class ProtontricksNativeDependency : ExecutableRuntimeDependency, IProtontricksDependency
{
    /// <inheritdoc />
    public override string DisplayName => "protontricks";
    /// <inheritdoc />
    public override string Description => "Manage Proton games and their dependencies.";
    /// <inheritdoc />
    public override Uri Homepage { get; } = new("https://github.com/Matoking/protontricks");

    /// <inheritdoc />
    public ProtontricksNativeDependency(IProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
    {
        var command = Cli.Wrap("protontricks").WithArguments("--version").WithStandardOutputPipe(outputPipeTarget);
        return command;
    }

    /// <inheritdoc />
    protected override RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output) => ToInformationImpl(output);

    internal static RuntimeDependencyInformation ToInformationImpl(ReadOnlySpan<char> output)
    {
        if (TryParseVersion(output, out var rawVersion, out var version))
        {
            return new RuntimeDependencyInformation
            {
                RawVersion = rawVersion,
                Version = version,
            };
        }

        return new RuntimeDependencyInformation();
    }

    public static bool TryParseVersion(ReadOnlySpan<char> output, out string? rawVersion, out Version? version)
    {
        // Example: "protontricks (1.11.1)"
        const string prefix = "protontricks (";
        const string suffix = ")";

        rawVersion = null;
        version = null;

        if (!output.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (output.Length < prefix.Length + suffix.Length) return false;

        var span = output[prefix.Length..];
        var index = span.IndexOf(suffix);

        if (index == -1) return false;

        span = span[..index];
        rawVersion = span.ToString();
        _ = Version.TryParse(rawVersion, out version);

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> MakeLaunchArgv(long appId, AbsolutePath exe, IReadOnlyList<string> args)
        => ["protontricks-launch", "--appid", appId.ToString(), exe.ToString(), .. args];
}
