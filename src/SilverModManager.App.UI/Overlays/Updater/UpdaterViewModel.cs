using SilverModManager.Sdk.Errors;
using System.Diagnostics;
using Avalonia.Threading;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.App.UI.Resources;
using SilverModManager.Sdk.Jobs;
using ReactiveUI.SourceGenerators;
using SilverModManager.App.UI.Settings;
using SilverModManager.Sdk.Settings;
using SilverModManager.Networking.GitHub;
using SilverModManager.Sdk;
using R3;

namespace SilverModManager.App.UI.Overlays.Updater;

/// <summary>
/// What the overlay needs to install an AppImage update in place.
/// </summary>
/// <param name="AppImagePath">The value of <c>$APPIMAGE</c>, null when the app does not run from an AppImage.</param>
internal record InAppUpdate(AppImageUpdateInstaller Installer, Asset Asset, string? AppImagePath, IAppRestarter Restarter, ILogger Logger);

[UsedImplicitly]
public partial class UpdaterViewModel : AOverlayViewModel<IUpdaterViewModel>, IUpdaterViewModel
{
    public ReactiveCommand CommandClose { get; }
    public ReactiveCommand CommandOpenReleaseInBrowser { get; }
    public ReactiveCommand CommandDownloadReleaseAssetInBrowser { get; }
    public ReactiveCommand CommandUpdateNow { get; }
    public ReactiveCommand CommandRestartNow { get; }
    public bool CanUpdateInApp { get; }

    [Reactive] public partial UpdateState State { get; private set; } = UpdateState.Available;
    [Reactive] public partial double Progress { get; private set; }
    [Reactive] public partial string? StatusMessage { get; private set; }

    public bool HasAsset { get; }
    public Version CurrentVersion { get; }
    public Version LatestVersion { get; }
    public InstallationMethod InstallationMethod { get; }

    internal UpdaterViewModel(
        IOSInterop osInterop,
        ISettingsManager settingsManager,
        InstallationMethod installationMethod,
        Version currentVersion,
        Version latestVersion,
        Uri releaseWebUri,
        Uri? assetDownloadUri,
        InAppUpdate? inAppUpdate = null)
    {
        _inAppUpdate = inAppUpdate;
        CanUpdateInApp = inAppUpdate is not null;

        CommandClose = new ReactiveCommand(_ =>
        {
            // A download that is still running is abandoned, which also removes its temporary file.
            CancelUpdate();

            // NOTE(erri120): something for later if we want to skip
            // settingsManager.Set(new UpdaterSettings
            // {
            //     VersionToSkip = latestVersion,
            // });

            base.Close();
        });

        CommandOpenReleaseInBrowser = new ReactiveCommand(_ => osInterop.OpenUri(releaseWebUri));
        CommandDownloadReleaseAssetInBrowser = Observable.Return(assetDownloadUri is not null).ToReactiveCommand(_ => osInterop.OpenUri(assetDownloadUri!));

        CommandUpdateNow = new ReactiveCommand(_ => UpdateNowAsync().FireAndForget(Logger));
        CommandRestartNow = new ReactiveCommand(_ => RestartNow());

        HasAsset = assetDownloadUri is not null;
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        InstallationMethod = installationMethod;
    }

    private readonly InAppUpdate? _inAppUpdate;
    private readonly CancellationTokenSource _cancellation = new();
    private ILogger Logger => _inAppUpdate?.Logger ?? NullLogger.Instance;

    internal void CancelUpdate() => _cancellation.Cancel();

    internal async Task UpdateNowAsync()
    {
        if (_inAppUpdate is null) throw new InvalidOperationException("This install cannot update itself.");
        if (State is UpdateState.Downloading or UpdateState.ReadyToRestart) return;

        State = UpdateState.Downloading;
        Progress = 0;
        StatusMessage = Language.Updater_Downloading;
        try
        {
            await _inAppUpdate.Installer.InstallAsync(_inAppUpdate.Asset, _inAppUpdate.AppImagePath, fraction => Progress = fraction, _cancellation.Token);
            State = UpdateState.ReadyToRestart;
            StatusMessage = Language.Updater_Installed;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
            StatusMessage = null;
        }
        catch (AppImageUpdateException e)
        {
            State = UpdateState.Failed;
            StatusMessage = e.Message;
        }
        catch (Exception e)
        {
            Logger.LogFailure(e, "Failed to install the update");
            State = UpdateState.Failed;
            StatusMessage = string.Format(Language.Updater_Failed, e.Message);
        }
    }

    internal void RestartNow()
    {
        if (State != UpdateState.ReadyToRestart || _inAppUpdate?.AppImagePath is not { } appImagePath) return;

        try
        {
            _inAppUpdate.Restarter.Restart(appImagePath);
        }
        catch (Exception e)
        {
            Logger.LogFailure(e, "Failed to restart into the updated app");
            State = UpdateState.Failed;
            StatusMessage = string.Format(Language.Updater_Failed, e.Message);
        }
    }

    public static async ValueTask<IUpdaterViewModel?> CreateIfNeeded(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        var currentVersion = ApplicationConstants.Version;
        var installationMethod = ApplicationConstants.InstallationMethod;

        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        if (!UpdateChecker.ShouldCheckForUpdate(currentVersion, installationMethod, settingsManager.Get<UpdaterSettings>())) return null;
        var updateChecker = serviceProvider.GetRequiredService<UpdateChecker>();

        var release = await updateChecker.FetchUpdateRelease(currentVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (release is null) return null;

        if (!release.TryGetVersion(out var latestVersion)) throw new UnreachableException();
        if (settingsManager.Get<UpdaterSettings>().VersionToSkip.Equals(latestVersion)) return null;

        updateChecker.TryGetMatchingReleaseAsset(release, installationMethod, out var releaseAsset);

        InAppUpdate? inAppUpdate = null;
        if (installationMethod is InstallationMethod.AppImage && releaseAsset is not null)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<AppImageUpdateInstaller>>();
            var installer = new AppImageUpdateInstaller(logger, (source, destination, progress, token) =>
                DownloadAsync(serviceProvider, source, new Uri(release.HtmlUrl), destination, progress, token));
            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            inAppUpdate = new InAppUpdate(installer, releaseAsset, appImagePath, new ProcessAppRestarter(serviceProvider.GetRequiredService<ILogger<ProcessAppRestarter>>()), logger);
        }

        return new UpdaterViewModel(
            osInterop: serviceProvider.GetRequiredService<IOSInterop>(),
            settingsManager: settingsManager,
            installationMethod: installationMethod,
            currentVersion: currentVersion,
            latestVersion: latestVersion,
            releaseWebUri: new Uri(release.HtmlUrl),
            assetDownloadUri: releaseAsset is null ? null : new Uri(releaseAsset.BrowserDownloadUrl),
            inAppUpdate: inAppUpdate
        );
    }

    private static async Task DownloadAsync(IServiceProvider serviceProvider, Uri source, Uri releasePage, string destination, Action<double> progress, CancellationToken cancellationToken)
    {
        var fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        var task = HttpDownloadJob.Create(serviceProvider, source, releasePage, fileSystem.FromUnsanitizedFullPath(destination));
        // The job reports from a pool thread, the overlay binds to the value.
        using var subscription = task.Job.ObservableProgress.Subscribe(percent =>
        {
            if (percent.HasValue) Dispatcher.UIThread.Post(() => progress(percent.Value.Value));
        });
        await using var registration = cancellationToken.Register(() => task.Job.AsContext().Cancel());
        await task;
    }

}

public enum UpdateState
{
    /// <summary>Nothing started yet, or an attempt was abandoned.</summary>
    Available,
    Downloading,
    /// <summary>The new AppImage is in place; the running one still is the old version.</summary>
    ReadyToRestart,
    Failed,
}
