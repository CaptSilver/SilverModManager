using Avalonia.Controls;
using JetBrains.Annotations;
using NexusMods.Paths;
using SilverModManager.Sdk;

namespace SilverModManager.App.UI.Overlays.Updater;

[UsedImplicitly]
public partial class UpdaterDesignView : UserControl
{
    public UpdaterDesignView()
    {
        InitializeComponent();

        ItemsControl.ItemsSource = new[]
        {
            Create(InstallationMethod.Archive, hasAsset: true),
            Create(InstallationMethod.AppImage, hasAsset: true),
            Create(InstallationMethod.Flatpak, hasAsset: false),
            Create(InstallationMethod.PackageManager, hasAsset: false),
        };
    }

    private static IUpdaterViewModel Create(InstallationMethod installationMethod, bool hasAsset)
    {
        return new UpdaterViewModel(
            osInterop: new DesignOsInterop(),
            installationMethod: installationMethod,
            currentVersion: new Version("1.0.0"),
            latestVersion: new Version("2.0.0"),
            releaseWebUri: new Uri("https://example.com"),
            assetDownloadUri: hasAsset ? new Uri("https://example.com") : null,
            inAppUpdate: null
        );
    }

    /// <summary>
    /// The cards only draw the update; every call that would leave the previewer is refused.
    /// </summary>
    private sealed class DesignOsInterop : IOSInterop
    {
        private const string DesignTimeOnly = "design-time only";

        public event Action<Uri>? OpenUriFailed
        {
            add => throw new NotSupportedException(DesignTimeOnly);
            remove => throw new NotSupportedException(DesignTimeOnly);
        }

        public AbsolutePath GetRunningExecutablePath(out string rawPath) => throw new NotSupportedException(DesignTimeOnly);
        public void OpenUri(Uri uri) => throw new NotSupportedException(DesignTimeOnly);
        public void OpenFile(AbsolutePath filePath) => throw new NotSupportedException(DesignTimeOnly);
        public void OpenDirectory(AbsolutePath directoryPath) => throw new NotSupportedException(DesignTimeOnly);
        public void OpenFileInDirectory(AbsolutePath filePath) => throw new NotSupportedException(DesignTimeOnly);
        public ValueTask<FileSystemMount[]> GetFileSystemMounts(CancellationToken cancellationToken = default) => throw new NotSupportedException(DesignTimeOnly);
        public ValueTask<FileSystemMount?> GetFileSystemMount(AbsolutePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException(DesignTimeOnly);
        public ValueTask RegisterUriSchemeHandler(string scheme, bool setAsDefaultHandler = true, CancellationToken cancellationToken = default) => throw new NotSupportedException(DesignTimeOnly);
    }
}
