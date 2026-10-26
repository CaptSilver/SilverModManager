using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <summary>
/// Plans a prefix setup and, for a write, carries it out. The seam the verb is tested through.
/// </summary>
public interface IPrefixSetupRunner
{
    /// <summary>
    /// Plans the setup and, for a write, carries it out. A step that fails after its attempts stops the run;
    /// the result says which and why, and the prefix keeps what earlier steps installed.
    /// </summary>
    /// <param name="installation">The game whose prefix is set up.</param>
    /// <param name="request">What to set up and whether to write.</param>
    /// <param name="cancellationToken">Cancels the run between steps.</param>
    /// <param name="stepFinished">Called as each step of a write ends, including steps that never started because an earlier one failed. Never called for a dry run.</param>
    Task<PrefixSetupResult> Run(GameInstallation installation, PrefixSetupRequest request, CancellationToken cancellationToken, Func<PrefixSetupStepResult, ValueTask>? stepFinished = null);
}
