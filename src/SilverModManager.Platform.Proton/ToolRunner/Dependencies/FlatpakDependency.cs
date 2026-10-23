using SilverModManager.Sdk;
using CliWrap;

namespace SilverModManager.Platform.Proton.ToolRunner.Dependencies;

/// <summary>
/// Flatpak itself, whatever is installed through it.
/// </summary>
public sealed class FlatpakDependency(IProcessRunner runner) : ProbedDependency(runner)
{
    public override string DisplayName => "flatpak";
    public override string Description => "Runs applications installed as Flatpaks.";
    public override Uri Homepage { get; } = new("https://flatpak.org/");

    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
        => Cli.Wrap("flatpak").WithArguments("--version").WithStandardOutputPipe(outputPipeTarget);
}
