using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>A tool started and stopped with a failing exit code; the message says how, in Wine's own words where there are any.</summary>
public sealed class ToolExitedException(string message, int exitCode, string strategy, Exception? inner = null) : UserFacingException(message, inner)
{
    /// <summary>The exit code of the program.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>The name of the strategy that ran the program.</summary>
    public string Strategy { get; } = strategy;

    /// <inheritdoc />
    public override string Title => "The tool stopped";
}
