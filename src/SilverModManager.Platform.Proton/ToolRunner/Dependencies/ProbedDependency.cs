using System.ComponentModel;
using CliWrap;
using DynamicData.Kernel;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.ToolRunner.Dependencies;

/// <summary>
/// An executable that is considered installed when a query command exits with zero.
/// </summary>
public abstract class ProbedDependency(IProcessRunner runner) : ExecutableRuntimeDependency(runner)
{
    /// <inheritdoc/>
    protected override RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output)
    {
        var version = output.Trim().ToString();
        return version.Length == 0 ? new RuntimeDependencyInformation() : new RuntimeDependencyInformation { RawVersion = version };
    }

    // The base class only treats a non-zero exit as missing. Starting a program that is not on PATH throws
    // instead, and that is the common way for one of these to be missing.
    /// <inheritdoc/>
    protected override async ValueTask<Optional<RuntimeDependencyInformation>> QueryInstallationInformationImpl(CancellationToken cancellationToken)
    {
        try
        {
            return await base.QueryInstallationInformationImpl(cancellationToken);
        }
        catch (Win32Exception)
        {
            return Optional<RuntimeDependencyInformation>.None;
        }
    }

    /// <summary>
    /// Whether the executable is installed. Blocks while the probe runs.
    /// </summary>
    /// <remarks>
    /// The probe runs on the thread pool so that blocking on it from a thread with a synchronization context,
    /// such as the UI thread, cannot deadlock on its continuations.
    /// </remarks>
    public bool IsInstalled() => Task.Run(() => QueryInstallationInformation(CancellationToken.None).AsTask()).GetAwaiter().GetResult().HasValue;

    /// <summary>
    /// The sentence that says what is missing and where to get it.
    /// </summary>
    public string MissingText => $"{DisplayName} is not installed or not on PATH, and is needed to run this program. See {Homepage}.";
}

/// <summary>
/// <c>umu-run</c>, the Unified Launcher for Windows Games on Linux.
/// </summary>
public sealed class UmuRunDependency(IProcessRunner runner) : ProbedDependency(runner)
{
    public override string DisplayName => "umu-run";
    public override string Description => "Runs Windows programs with a Proton build outside of Steam.";
    public override Uri Homepage { get; } = new("https://github.com/Open-Wine-Components/umu-launcher");

    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
        => Cli.Wrap("umu-run").WithArguments("--version").WithStandardOutputPipe(outputPipeTarget);
}

/// <summary>
/// A <c>wine</c> executable, either the one on PATH or the one a launcher picked for a game.
/// </summary>
public sealed class WineDependency(IProcessRunner runner, string binary) : ProbedDependency(runner)
{
    public override string DisplayName => binary;
    public override string Description => "Runs Windows programs.";
    public override Uri Homepage { get; } = new("https://www.winehq.org/");

    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
        => Cli.Wrap(binary).WithArguments("--version").WithStandardOutputPipe(outputPipeTarget);
}

/// <summary>
/// The Flatpak build of Heroic, which carries the runners its games use.
/// </summary>
public sealed class HeroicFlatpakDependency(IProcessRunner runner) : ProbedDependency(runner)
{
    public const string FlatpakId = "com.heroicgameslauncher.hgl";

    public override string DisplayName => "Heroic Games Launcher (flatpak)";
    public override string Description => "Runs the games installed through the Flatpak build of Heroic.";
    public override Uri Homepage { get; } = new("https://heroicgameslauncher.com/");

    // Fails when flatpak itself is missing, or when Heroic is not installed through it.
    protected override Command BuildQueryCommand(PipeTarget outputPipeTarget)
        => Cli.Wrap("flatpak").WithArguments(["info", FlatpakId]).WithStandardOutputPipe(outputPipeTarget);
}
