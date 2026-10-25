using System.Collections.Immutable;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <param name="Proceed">Whether the cleanup may go on. False when the user cancelled.</param>
/// <param name="Report">What was downloaded, opened in the browser or failed.</param>
public sealed record SaveOutcome(bool Proceed, SaveReport Report);

/// <summary>
/// The step that runs before the cleanup deletes anything: shows what would be lost, downloads what the user keeps
/// checked, and says what failed. The cleanup runs only if this returns <see cref="SaveOutcome.Proceed"/>.
/// </summary>
public static class SaveBeforeCleanup
{
    public static async Task<SaveOutcome> RunAsync(SavePlan plan, IModDownloader downloader, ISavePrompts prompts, CancellationToken cancellationToken)
    {
        if (plan.IsEmpty) return new SaveOutcome(true, SaveReport.None);

        var chosen = await prompts.ChooseAsync(plan, cancellationToken);
        if (chosen is null) return new SaveOutcome(false, SaveReport.None);

        // Only what the list offered; the prompt cannot add a download of its own.
        var wanted = plan.Save.Where(chosen.Value.Contains).ToArray();
        var game = wanted.Length == 0 ? default : plan.Game ?? throw new InvalidOperationException("A save plan lists downloads for a game with no Nexus Mods id.");

        var added = ImmutableArray.CreateBuilder<ModToSave>();
        var opened = ImmutableArray.CreateBuilder<ModToSave>();
        var failed = ImmutableArray.CreateBuilder<FailedDownload>();
        foreach (var mod in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DownloadAsync(downloader, game, mod, cancellationToken);
            switch (result.Kind)
            {
                case ModDownloadKind.Added: added.Add(mod); break;
                case ModDownloadKind.OpenedInBrowser: opened.Add(mod); break;
                default: failed.Add(new FailedDownload(mod, result.Message ?? "The download failed.")); break;
            }
        }

        var report = new SaveReport(added.ToImmutable(), opened.ToImmutable(), failed.ToImmutable());

        // A page opened for a free account is not a failure: the file arrives when the user downloads it there.
        var proceed = report.Failed.IsEmpty || await prompts.ContinueAfterFailuresAsync(report, cancellationToken);
        return new SaveOutcome(proceed, report);
    }

    private static async Task<ModDownloadResult> DownloadAsync(IModDownloader downloader, Sdk.NexusModsApi.NexusModsGameId game, ModToSave mod, CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.DownloadAsync(game, mod, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ModDownloadResult.Failed(e.Message);
        }
    }
}
