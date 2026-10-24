using SilverModManager.Sdk;
using R3;

namespace SilverModManager.App.UI.Overlays.Updater;

public interface IUpdaterViewModel : IOverlayViewModel
{
    ReactiveCommand CommandClose { get; }
    ReactiveCommand CommandOpenReleaseInBrowser { get; }
    ReactiveCommand CommandDownloadReleaseAssetInBrowser { get; }
    ReactiveCommand CommandUpdateNow { get; }
    ReactiveCommand CommandRestartNow { get; }
    bool HasAsset { get; }

    /// <summary>
    /// Whether the app can download and install this release itself.
    /// </summary>
    bool CanUpdateInApp { get; }

    UpdateState State { get; }

    /// <summary>
    /// Download progress from 0 to 1.
    /// </summary>
    double Progress { get; }

    string? StatusMessage { get; }

    Version CurrentVersion { get; }
    Version LatestVersion { get; }
    InstallationMethod InstallationMethod { get; }
}
