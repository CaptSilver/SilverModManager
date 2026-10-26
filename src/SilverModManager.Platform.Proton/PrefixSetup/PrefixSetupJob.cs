using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <summary>
/// Runs a <see cref="PrefixSetupRunner"/> as a job, so the UI can show its progress and cancel it. A step that
/// fails fails the job with a message for the user; a dry run completes with the plan.
/// </summary>
public sealed class PrefixSetupJob(PrefixSetupRunner runner, GameInstallation installation, PrefixSetupRequest request)
    : IJobDefinitionWithStart<PrefixSetupJob, PrefixSetupResult>
{
    /// <inheritdoc/>
    public async ValueTask<PrefixSetupResult> StartAsync(IJobContext<PrefixSetupJob> context)
    {
        var result = await runner.Run(installation, request, context.CancellationToken);
        if (result.Failure is { } failure)
            throw new PrefixSetupFailedException($"Setting up {failure.Name} failed: {failure.Error}");

        return result;
    }
}
