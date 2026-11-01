using System.Diagnostics;
using System.Reactive.Disposables;
using DynamicData;
using DynamicData.Kernel;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using R3;
using ReactiveUI;

namespace SilverModManager.Library;

/// <summary>
/// Implementation of <see cref="IDownloadsService"/>.
/// </summary>
public sealed class DownloadsService : IDownloadsService, IDisposable
{
    // Shown when neither the metadata nor the destination names the download.
    private const string UnknownDownloadName = "Unknown Download";

    private readonly IJobMonitor _jobMonitor;
    private readonly IConnection _connection;
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
    
    private readonly SourceCache<DownloadInfo, DownloadId> _downloadCache = new(x => x.Id);
    
    /// <summary>
    /// Constructor.
    /// </summary>
    public DownloadsService(
        IJobMonitor jobMonitor,
        IConnection connection)
    {
        _jobMonitor = jobMonitor;
        _connection = connection;

        InitializeObservables();
    }

    private void InitializeObservables()
    {
        // TODO: Restore completed downloads from persistent storage on application boot.

        // Monitor every tracked download job (Nexus Mods and the file hosts a modlist's archives come from) and
        // transform them into DownloadInfo. Completed and failed downloads are kept in the cache when the
        // JobMonitor removes them, so they stay on the page.
        _jobMonitor.GetObservableChangeSet<ITrackedDownloadJob>()
            .Subscribe(changes =>
            {
                _downloadCache.Edit(updater =>
                {
                    foreach (var change in changes)
                    {
                        switch (change.Reason)
                        {
                            case ChangeReason.Add:
                            case ChangeReason.Update:
                            case ChangeReason.Refresh:
                                var trackedJob = (ITrackedDownloadJob)change.Current.Definition;
                                var transferJob = TransferJobOf(change.Current);
                                var downloadInfo = CreateDownloadInfo(trackedJob, transferJob, change.Current.Id);
                                updater.AddOrUpdate(downloadInfo, change.Current.Id);
                                
                                // Subscribe to job observables for reactive updates
                                if (change.Reason == ChangeReason.Add)
                                    SubscribeToJobObservables(transferJob, downloadInfo);

                                break;
                            case ChangeReason.Remove:
                                // Note(sewer): JobMonitor removes all jobs after completion, but we want to keep the completed jobs
                                //              to show in the 'Completed' tab.
                                //              Cancelled jobs can also yield a ChangeReason.Remove, so we need to make a distinction here. 
                                if (change.Current.Status == JobStatus.Completed)
                                {
                                    // Keep completed downloads but mark them as completed
                                    var existingItem = updater.Lookup(change.Key);
                                    if (existingItem.HasValue)
                                    {
                                        var completedDownload = existingItem.Value;
                                        MarkJobAsCompleted(completedDownload);
                                        // Clean up observable subscriptions
                                        completedDownload.Subscriptions?.Dispose();
                                    }
                                }
                                else if (change.Current.Status == JobStatus.Failed)
                                {
                                    // A failed download stays on the page with its reason; the user has nothing else to read it from.
                                    var existingItem = updater.Lookup(change.Key);
                                    if (existingItem.HasValue)
                                    {
                                        MarkJobAsFailed(existingItem.Value, change.Current);
                                        existingItem.Value.Subscriptions?.Dispose();
                                    }
                                }
                                else
                                {
                                    // Remove non-completed downloads normally
                                    var existingItem = updater.Lookup(change.Key);
                                    if (existingItem.HasValue)
                                        existingItem.Value.Subscriptions?.Dispose();

                                    updater.RemoveKey(change.Key);
                                }
                                break;
                            case ChangeReason.Moved:
                                // Nothing to do for moves
                                break;
                        }
                    }
                });
            })
            .DisposeWith(_disposables);
    }
    
    // Observable properties implementation
    public IObservable<IChangeSet<DownloadInfo, DownloadId>> ActiveDownloads => 
        _downloadCache.Connect()
            .FilterOnObservable(x => x.Status.AsObservable().Select(status => status.IsActive()).AsSystemObservable())
            .RefCount();
    
    public IObservable<IChangeSet<DownloadInfo, DownloadId>> CompletedDownloads =>
        _downloadCache.Connect()
            .FilterOnObservable(x => x.Status.AsObservable().Select(status => status == JobStatus.Completed).AsSystemObservable())
            .RefCount();
    
    public IObservable<IChangeSet<DownloadInfo, DownloadId>> AllDownloads =>
        _downloadCache.Connect();
    
    public IObservable<IChangeSet<DownloadInfo, DownloadId>> GetDownloadsForGame(NexusModsGameId nexusModsGameId) =>
        _downloadCache.Connect()
            .Filter(x => x.GameId.Value.Equals(nexusModsGameId));
    
    public IObservable<IChangeSet<DownloadInfo, DownloadId>> GetActiveDownloadsForGame(NexusModsGameId nexusModsGameId) =>
        _downloadCache.Connect()
            .FilterOnObservable(x => x.Status.AsObservable().Select(status => x.GameId.Value.Equals(nexusModsGameId) && status.IsActive()).AsSystemObservable())
            .RefCount();
    
    /// <summary>
    /// The job that moves the bytes for a tracked download: the job itself, or the one it hands the transfer to
    /// (the HTTP job inside a Nexus Mods download). It reports the progress and takes pause, resume and cancel.
    /// </summary>
    private static IJob TransferJobOf(IJob job) =>
        job.Definition is ITrackedDownloadJob { Transfer: { } transfer } ? transfer.Job : job;

    private JobId ResolveToTransferJobId(DownloadInfo downloadInfo)
    {
        var job = _jobMonitor.Find(downloadInfo.Id);
        return job is null ? downloadInfo.Id : TransferJobOf(job).Id;
    }

    // Control operations
    
    public void PauseDownload(DownloadInfo downloadInfo)
    {
        if (downloadInfo.SupportsPausing.Value) _jobMonitor.Pause(ResolveToTransferJobId(downloadInfo));
    }
    
    public void ResumeDownload(DownloadInfo downloadInfo) => _jobMonitor.Resume(ResolveToTransferJobId(downloadInfo));
    
    public void CancelDownload(DownloadInfo downloadInfo) => _jobMonitor.Cancel(ResolveToTransferJobId(downloadInfo));
    public void PauseAll()
    {
        foreach (var download in _downloadCache.Items.Where(d => d.Status.Value == JobStatus.Running && d.SupportsPausing.Value))
            _jobMonitor.Pause(ResolveToTransferJobId(download));
    }

    public void PauseAllForGame(NexusModsGameId nexusModsGameId)
    {
        foreach (var download in _downloadCache.Items.Where(d => 
            d.Status.Value == JobStatus.Running && d.SupportsPausing.Value && d.GameId.Value.Equals(nexusModsGameId)))
            _jobMonitor.Pause(ResolveToTransferJobId(download));
    }

    public void ResumeAll()
    {
        foreach (var download in _downloadCache.Items.Where(d => d.Status.Value == JobStatus.Paused))
            _jobMonitor.Resume(ResolveToTransferJobId(download));
    }

    public void ResumeAllForGame(NexusModsGameId nexusModsGameId)
    {
        foreach (var download in _downloadCache.Items.Where(d => 
            d.Status.Value == JobStatus.Paused && d.GameId.Value.Equals(nexusModsGameId)))
            _jobMonitor.Resume(ResolveToTransferJobId(download));
    }
    
    public void CancelRange(IEnumerable<DownloadInfo> downloads)
    {
        foreach (var download in downloads)
            _jobMonitor.Cancel(ResolveToTransferJobId(download));
    }
    
    private DownloadInfo CreateDownloadInfo(ITrackedDownloadJob trackedJob, IJob transferJob, JobId currentId)
    {
        var info = new DownloadInfo 
        { 
            Id = currentId,
        };
        
        // Set initial values using internal methods
        info.SetName(ExtractName(trackedJob));
        info.SetSourceName(trackedJob.SourceName);
        info.SetGroupName(trackedJob.GroupName);
        info.SetDownloadPageUri(trackedJob.PageUri is { } page ? page : Optional<Uri>.None);
        info.SetSupportsPausing(transferJob.Definition.SupportsPausing);
        // Only Nexus Mods downloads belong to a game and have file metadata to find an icon from.
        if (trackedJob is INexusModsDownloadJob nexusJob)
        {
            info.SetGameId(nexusJob.FileMetadata.Uid.GameId);
            info.SetFileMetadataId(nexusJob.FileMetadata.Id);
        }
        // FileSize, Progress, DownloadedBytes, TransferRate, Status, CompletedAt are set by observable subscriptions
        
        return info;
    }

    private void SubscribeToJobObservables(IJob transferJob, DownloadInfo downloadInfo)
    {
        // Ensure we don't have existing subscriptions for this job
        downloadInfo.Subscriptions?.Dispose();
        
        var jobDisposables = new System.Reactive.Disposables.CompositeDisposable();
        
        // Subscribe to progress changes
        transferJob.ObservableProgress
            .Subscribe(progress => downloadInfo.SetProgress(progress.HasValue ? progress.Value : Percent.Zero))
            .DisposeWith(jobDisposables);
        
        // Subscribe to rate of progress changes
        transferJob.ObservableRateOfProgress
            .Subscribe(rateOfProgress => downloadInfo.SetTransferRate(Size.FromLong((long)(rateOfProgress.HasValue ? rateOfProgress.Value : 0))))
            .DisposeWith(jobDisposables);
        
        // Subscribe to status changes
        transferJob.ObservableStatus
            .Subscribe(status => downloadInfo.SetStatus(status))
            .DisposeWith(jobDisposables);
        
        // A job that cannot say how many bytes it has moved shows progress only
        if (transferJob.GetJobStateData<IHttpDownloadState>() is { } state)
        {
            // Subscribe to ContentLength changes (FileSize)
            state.WhenAnyValue(x => x.ContentLength)
                .Subscribe(contentLength => downloadInfo.SetFileSize(contentLength.HasValue ? contentLength.Value : Size.From(0)))
                .DisposeWith(jobDisposables);

            // Subscribe to TotalBytesDownloaded changes (DownloadedBytes)
            state.WhenAnyValue(x => x.TotalBytesDownloaded)
                .Subscribe(totalBytes => downloadInfo.SetDownloadedBytes(totalBytes))
                .DisposeWith(jobDisposables);
        }
        
        // Store the subscription for later disposal
        downloadInfo.Subscriptions = jobDisposables;
    }

    private static void MarkJobAsCompleted(DownloadInfo downloadInfo)
    {
        // Reset transient properties that are only relevant for active downloads
        downloadInfo.SetTransferRate(Size.From(0));
        
        // A host that gave no length never reported progress
        downloadInfo.SetProgress(Percent.One);

        // Set completion timestamp
        downloadInfo.SetCompletedAt(DateTimeOffset.UtcNow);
        
        // Keep Progress at 100% and other completed state
    }

    private static void MarkJobAsFailed(DownloadInfo downloadInfo, IJob job)
    {
        downloadInfo.SetTransferRate(Size.From(0));
        downloadInfo.SetStatus(JobStatus.Failed);
        downloadInfo.SetFailureReason(job.AsContext().TryGetException(out var exception) && exception is not null
            ? exception.Message
            : null);
    }

    // Helper methods
    private static string ExtractName(ITrackedDownloadJob trackedJob)
    {
        var name = trackedJob.DisplayName;
        if (!string.IsNullOrEmpty(name))
            return name;
        
        // Note(sewer): The name should never be empty in practice, as a Nexus download fetches the metadata before
        // starting and a modlist archive carries its name, however; as a precaution; we provide a fallback here.

        // Fallback to destination filename as absolute last resort.
        var destination = trackedJob.Destination;
        if (destination == default(AbsolutePath))
            return UnknownDownloadName;

        var destinationFileName = destination.FileName;
        if (string.IsNullOrEmpty(destinationFileName))
            return UnknownDownloadName;
        
        var nameWithoutExt = Path.GetFileNameWithoutExtension(destinationFileName);
        return nameWithoutExt.Replace('_', ' ').Replace('-', ' ');
    }

    public Optional<LibraryFile.ReadOnly> ResolveLibraryFile(DownloadInfo downloadInfo)
    {
        // Only resolve for completed downloads
        if (downloadInfo.Status.Value != JobStatus.Completed)
            return Optional<LibraryFile.ReadOnly>.None;
        
        // Only Nexus Mods downloads have file metadata to find the library file from.
        if (downloadInfo.FileMetadataId.Value == default(EntityId))
            return Optional<LibraryFile.ReadOnly>.None;

        try
        {
            // Retrieve the FileMetadata from the database using the stored EntityId
            var fileMetadata = new NexusModsFileMetadata.ReadOnly(_connection.Db, downloadInfo.FileMetadataId.Value);
            
            // Find library items that match this file metadata
            var libraryItems = NexusModsLibraryItem.FindByFileMetadata(_connection.Db, fileMetadata);
            if (libraryItems.Count == 0)
                return Optional<LibraryFile.ReadOnly>.None;
            
            // Convert the first library item to LibraryFile.ReadOnly
            var libraryItem = libraryItems.First().AsLibraryItem();
            return !libraryItem.TryGetAsLibraryFile(out var libraryFile) 
                ? Optional<LibraryFile.ReadOnly>.None
                : Optional<LibraryFile.ReadOnly>.Create(libraryFile);
        }
        catch (Exception)
        {
            // Any database error results in None
            return Optional<LibraryFile.ReadOnly>.None;
        }
    }

    
    public void Dispose()
    {
        // Dispose all job subscriptions
        foreach (var downloadInfo in _downloadCache.Items)
            downloadInfo.Subscriptions?.Dispose();
        
        _disposables.Dispose();
        _downloadCache.Dispose();
    }
}
