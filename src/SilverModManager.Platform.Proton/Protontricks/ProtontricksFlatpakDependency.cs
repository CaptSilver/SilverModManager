using CliWrap;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Protontricks;

/// <summary>
///     Protontricks is a tool that helps manage Proton games and their dependencies.
///     This implementation specifically targets the Flatpak version of Protontricks,
///     commonly found on Steam Deck (SteamOS) and other Linux distributions.
/// </summary>
public class ProtontricksFlatpakDependency : ExecutableRuntimeDependency, IProtontricksDependency
{
    private const string FlatpakPackageId = "com.github.Matoking.protontricks";

    /// <inheritdoc />
    public override string DisplayName => "protontricks (flatpak)";
    /// <inheritdoc />
    public override string Description => "Manage Proton games and their dependencies (Flatpak version)";
    /// <inheritdoc />
    public override Uri Homepage { get; } = new("https://github.com/Matoking/protontricks");

    /// <inheritdoc />
    public ProtontricksFlatpakDependency(IProcessRunner runner) : base(runner) { }

    /// <inheritdoc />
    protected override RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output) => ProtontricksNativeDependency.ToInformationImpl(output);

    /// <inheritdoc />
    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
    {
        // This will return a non-zero code if neither flatpak or protontricks are installed
        // Which in turn will be recognised as a missing dependency.
        var command = Cli.Wrap("flatpak")
            .WithArguments($"run {FlatpakPackageId} --version")
            .WithStandardOutputPipe(outputPipeTarget);
        return command;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> MakeLaunchArgv(long appId, AbsolutePath exe, IReadOnlyList<string> args)
        => ["flatpak", "run", "--command=protontricks-launch", FlatpakPackageId, "--appid", appId.ToString(), exe.ToString(), .. args];
}
