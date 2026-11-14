using CliWrap;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.ToolRunner.Dependencies;

/// <summary>
/// The Flatpak build of Lutris, which a game installed through it can only be started from.
/// </summary>
public class LutrisFlatpakDependency(IProcessRunner runner) : ProbedDependency(runner)
{
    public const string FlatpakId = "net.lutris.Lutris";

    public override string DisplayName => "Lutris (flatpak)";
    public override string Description => "Starts the games installed through the Flatpak build of Lutris.";
    public override Uri Homepage { get; } = new("https://lutris.net/");

    // Fails when flatpak itself is missing, or when Lutris is not installed through it.
    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
        => Cli.Wrap("flatpak").WithArguments(["info", FlatpakId]).WithStandardOutputPipe(outputPipeTarget);
}
