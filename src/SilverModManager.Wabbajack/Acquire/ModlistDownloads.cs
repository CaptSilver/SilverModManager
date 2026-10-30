using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>What starting a modlist's downloads came to.</summary>
/// <param name="AlreadyRunning">The list's downloads were already under way, so nothing was started.</param>
/// <param name="Counts">How the list's archives stand, from the plan the run was started with (or is working on).</param>
public sealed record DownloadStart(ModlistInfo Modlist, bool AlreadyRunning, PlanCounts Counts);

/// <summary>Another modlist's downloads are under way, and the acquirer works on one list at a time.</summary>
public sealed class DownloadBusyException(string message) : UserFacingException(message)
{
    public override string Title => "Downloads already running";
}

/// <summary>
/// Starts a modlist's archive downloads without a page, for the command line and for the restart after a close, and
/// runs them in the app's acquirer, so the modlist page shows them like any other batch.
/// </summary>
public sealed class ModlistDownloads(
    ISettingsManager settings,
    IModlistPlanner planner,
    IArchiveAcquirer acquirer,
    IModlistCatalog catalog,
    ILogger<ModlistDownloads> logger)
{
    private readonly Lock _lock = new();
    private LibraryFileId? _starting;

    /// <summary>
    /// Plans the list against the library, starts the run and returns without waiting for it. The run belongs to the
    /// app, not to the caller: a command that disconnects does not stop it. <see cref="Cancel"/> does.
    /// </summary>
    /// <exception cref="DownloadBusyException">Another list's downloads are under way.</exception>
    public async Task<DownloadStart> Start(ModlistInfo modlist, CancellationToken cancellationToken)
    {
        if (Claim(modlist) is { } running) return running;

        try
        {
            var plan = await planner.BuildPlan(modlist.Id, null, null, cancellationToken);
            var counts = PlanCounts.Of(plan);
            var run = acquirer.Run(plan, null, CancellationToken.None);
            _ = Observe(run, modlist.Name);
            return new DownloadStart(modlist, false, counts);
        }
        finally
        {
            lock (_lock) _starting = null;
        }
    }

    /// <summary>Takes the right to start a run, or says why not: this list is already on, or another one is.</summary>
    private DownloadStart? Claim(ModlistInfo modlist)
    {
        lock (_lock)
        {
            var busy = _starting ?? (acquirer.Status is { IsRunning: true } status ? status.Modlist : null);
            if (busy is null)
            {
                _starting = modlist.Id;
                return null;
            }

            if (busy == modlist.Id) return new DownloadStart(modlist, true, PlanCounts.Of(acquirer.Plan));
            var other = catalog.List().FirstOrDefault(m => m.Id == busy)?.Name ?? "another modlist";
            throw new DownloadBusyException($"The downloads of '{other}' are running. Cancel them first, or wait for them to finish.");
        }
    }

    /// <summary>Stops the downloads that are under way, whichever list they are for.</summary>
    public void Cancel() => acquirer.Cancel();

    /// <summary>
    /// Starts again the downloads the last session left unfinished, if it left any. Archives already in the library
    /// plan as present and partial downloads resume where the download job can, so this repeats only what is missing.
    /// Returns when the run ends; <paramref name="cancellationToken"/> is the app closing, which keeps the flag.
    /// </summary>
    public async Task Resume(CancellationToken cancellationToken)
    {
        if (PendingDownload.Read(settings) is not { } id) return;

        var modlist = catalog.List().FirstOrDefault(m => m.Id == id);
        if (modlist is null)
        {
            logger.LogWarning("The download that was under way before the restart is for a modlist that is no longer in the library; dropping it");
            PendingDownload.Set(settings, null);
            return;
        }

        logger.LogInformation("Resuming the download of '{List}' after a restart", modlist.Name);
        try
        {
            var plan = await planner.BuildPlan(id, null, null, cancellationToken);
            await acquirer.Run(plan, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is closing again; the flag stays for the start after this one.
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("The resumed download of '{List}' was cancelled", modlist.Name);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not resume the download of '{List}': {Reason}", modlist.Name, e.Message);
            PendingDownload.Set(settings, null);
        }
    }

    private async Task Observe(Task run, string name)
    {
        try
        {
            await run;
            logger.LogInformation("The download of '{List}' finished", name);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("The download of '{List}' was cancelled", name);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The download of '{List}' failed: {Reason}", name, e.Message);
        }
    }
}

/// <summary>Picks up the downloads the last session left unfinished, once the app is up.</summary>
public sealed class ModlistDownloadResumer(ModlistDownloads downloads, ILogger<ModlistDownloadResumer> logger) : IHostedService
{
    private readonly CancellationTokenSource _closing = new();
    private Task _resume = Task.CompletedTask;

    // The run can take hours and planning a big list takes minutes; neither may hold up the start of the app.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _resume = Task.Run(async () =>
        {
            try
            {
                await downloads.Resume(_closing.Token);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Resuming the interrupted download failed");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _closing.CancelAsync();
        await _resume.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
