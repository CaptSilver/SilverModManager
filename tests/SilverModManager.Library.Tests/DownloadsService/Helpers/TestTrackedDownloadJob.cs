using SilverModManager.Abstractions.Downloads;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Tests.DownloadsService.Helpers;

/// <summary>
/// A download from a file host: it moves the bytes itself, so the monitor's job for it carries the progress and takes
/// the cancel. The test finishes or fails it through <see cref="Outcome"/>.
/// </summary>
public record TestTrackedDownloadJob : ITrackedDownloadJob, IJobDefinitionWithStart<TestTrackedDownloadJob, AbsolutePath>
{
    public required AbsolutePath Destination { get; init; }
    public string DisplayName { get; init; } = "Some Archive.7z";
    public string SourceName { get; init; } = "MEGA";
    public Uri? PageUri { get; init; } = new("https://mega.example/file/abc");
    public string? GroupName { get; init; }
    public bool SupportsPausing { get; init; }

    public TaskCompletionSource<AbsolutePath> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ReportedDownloadState State { get; } = new();

    public IPublicJobStateData? GetJobStateData() => State;

    public async ValueTask<AbsolutePath> StartAsync(IJobContext<TestTrackedDownloadJob> context)
    {
        await context.YieldAsync();
        using var _ = context.CancellationToken.Register(() => Outcome.TrySetCanceled(context.CancellationToken));
        return await Outcome.Task;
    }

    public ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile) => ValueTask.CompletedTask;
}
