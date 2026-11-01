using System.Runtime.CompilerServices;
using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;
using R3;

[assembly: InternalsVisibleTo("SilverModManager.Library")]
[assembly: InternalsVisibleTo("SilverModManager.Library.Tests")]
[assembly: InternalsVisibleTo("SilverModManager.UI.Tests")]

namespace SilverModManager.Abstractions.Downloads;

/// <summary>
/// Represents download information for UI display.
/// Thread safety is not guaranteed - UI consumers should use OnUI() or similar.
/// </summary>
[PublicAPI]
public class DownloadInfo : IDisposable
{
    /// <remarks>
    ///     The value of this field is <see cref="IJob.Id"/> for the 'NexusModsDownloadJob'.
    ///     We're trusting that GUIDs are unique enough to never (in practice) make a duplicate across multiple runs/reboots. 
    /// </remarks>
    public required DownloadId Id { get; init; }
    
    // Backing fields for reactive properties
    private readonly BindableReactiveProperty<string> _name = new(string.Empty);
    private readonly BindableReactiveProperty<NexusModsGameId> _gameId = new(default(NexusModsGameId));
    private readonly BindableReactiveProperty<Size> _fileSize = new(Size.From(0));
    private readonly BindableReactiveProperty<Percent> _progress = new(Percent.Zero);
    private readonly BindableReactiveProperty<Size> _downloadedBytes = new(Size.From(0));
    private readonly BindableReactiveProperty<Size> _transferRate = new(Size.From(0));
    private readonly BindableReactiveProperty<JobStatus> _status = new(default(JobStatus));
    private readonly BindableReactiveProperty<Optional<Uri>> _downloadPageUri = new(default(Optional<Uri>));
    private readonly BindableReactiveProperty<Optional<DateTimeOffset>> _completedAt = new(default(Optional<DateTimeOffset>));
    private readonly BindableReactiveProperty<EntityId> _fileMetadataId = new(default(EntityId));
    private readonly BindableReactiveProperty<string> _sourceName = new(string.Empty);
    private readonly BindableReactiveProperty<string?> _groupName = new(null);
    private readonly BindableReactiveProperty<string?> _failureReason = new(null);
    private readonly BindableReactiveProperty<bool> _supportsPausing = new(false);
    
    /// <summary>
    /// Display name of the download (typically mod name).
    /// </summary>
    public IReadOnlyBindableReactiveProperty<string> Name => _name;
    
    /// <summary>
    /// The game this download is for.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<NexusModsGameId> GameId => _gameId;
    
    /// <summary>
    /// Total file size.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Size> FileSize => _fileSize;
    
    /// <summary>
    /// Download progress percentage.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Percent> Progress => _progress;
    
    /// <summary>
    /// Downloaded bytes so far.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Size> DownloadedBytes => _downloadedBytes;
    
    /// <summary>
    /// Current transfer rate.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Size> TransferRate => _transferRate;
    
    /// <summary>
    /// Current job status.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<JobStatus> Status => _status;
    
    /// <summary>
    /// The page URL where the download originated.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Optional<Uri>> DownloadPageUri => _downloadPageUri;
    
    /// <summary>
    /// When the download completed.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<Optional<DateTimeOffset>> CompletedAt => _completedAt;
    
    /// <summary>
    /// Entity ID of the file metadata associated with this download.
    /// </summary>
    /// <remarks>
    /// We store the EntityId rather than NexusModsFileMetadata.ReadOnly to avoid circular dependency issues.
    /// The full FileMetadata can be retrieved from the database using this ID.
    /// </remarks>
    public IReadOnlyBindableReactiveProperty<EntityId> FileMetadataId => _fileMetadataId;
    
    /// <summary>
    /// Where the file comes from as the user knows it, such as "Nexus Mods" or "MEGA".
    /// </summary>
    public IReadOnlyBindableReactiveProperty<string> SourceName => _sourceName;

    /// <summary>
    /// What the download is part of, such as the name of a modlist; null when it stands alone.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<string?> GroupName => _groupName;

    /// <summary>
    /// Why the download failed; null until it has.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<string?> FailureReason => _failureReason;

    /// <summary>
    /// Whether the download can be paused and resumed; a host that can only start over cannot.
    /// </summary>
    public IReadOnlyBindableReactiveProperty<bool> SupportsPausing => _supportsPausing;

    /// <summary>
    /// Internal subscription management for reactive updates.
    /// </summary>
    internal System.Reactive.Disposables.CompositeDisposable? Subscriptions { get; set; }

    // Internal mutation methods for DownloadsService
    internal void SetName(string value) => _name.Value = value;
    internal void SetGameId(NexusModsGameId value) => _gameId.Value = value;
    internal void SetFileSize(Size value) => _fileSize.Value = value;
    internal void SetProgress(Percent value) => _progress.Value = value;
    internal void SetDownloadedBytes(Size value) => _downloadedBytes.Value = value;
    internal void SetTransferRate(Size value) => _transferRate.Value = value;
    internal void SetStatus(JobStatus value) => _status.Value = value;
    internal void SetDownloadPageUri(Optional<Uri> value) => _downloadPageUri.Value = value;
    internal void SetCompletedAt(Optional<DateTimeOffset> value) => _completedAt.Value = value;
    internal void SetFileMetadataId(EntityId value) => _fileMetadataId.Value = value;
    internal void SetSourceName(string value) => _sourceName.Value = value;
    internal void SetGroupName(string? value) => _groupName.Value = value;
    internal void SetFailureReason(string? value) => _failureReason.Value = value;
    internal void SetSupportsPausing(bool value) => _supportsPausing.Value = value;

    private bool _isDisposed;
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                Subscriptions?.Dispose();
                _name?.Dispose();
                _gameId?.Dispose();
                _fileSize?.Dispose();
                _progress?.Dispose();
                _downloadedBytes?.Dispose();
                _transferRate?.Dispose();
                _status?.Dispose();
                _downloadPageUri?.Dispose();
                _completedAt?.Dispose();
                _fileMetadataId?.Dispose();
                _sourceName?.Dispose();
                _groupName?.Dispose();
                _failureReason?.Dispose();
                _supportsPausing?.Dispose();
            }

            _isDisposed = true;
        }
    }
}
