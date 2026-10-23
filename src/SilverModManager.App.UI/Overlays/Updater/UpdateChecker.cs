using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Settings;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Overlays.Updater;

public class UpdateChecker
{
    private const string GitHubOrganization = "CaptSilver";
    private const string GitHubRepository = "SilverModManager";

    private readonly ILogger _logger;
    private readonly IGitHubApi _gitHubApi;

    public UpdateChecker(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<UpdateChecker>>();
        _gitHubApi = serviceProvider.GetRequiredService<IGitHubApi>();
    }

    public static bool ShouldCheckForUpdate(ISettingsManager settingsManager)
    {
        return ShouldCheckForUpdate(ApplicationConstants.Version, ApplicationConstants.InstallationMethod, settingsManager.Get<UpdaterSettings>());
    }

    public static bool ShouldCheckForUpdate(Version currentVersion, InstallationMethod installationMethod, UpdaterSettings settings)
    {
        // The release feed is opt-in: no request leaves the machine unless the user turned it on.
        if (!settings.ReleaseFeedEnabled) return false;

        // NOTE(erri120): don't check for updates if build from source
        return installationMethod is not InstallationMethod.Manually;
    }

    public async ValueTask<Release?> FetchUpdateRelease(Version currentVersion, CancellationToken cancellationToken = default)
    {
        var latestRelease = await _gitHubApi.FetchLatestRelease(GitHubOrganization, GitHubRepository, comparer: ReleaseTagComparer.Instance, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (latestRelease is null) return null;

        if (!latestRelease.TryGetVersion(out var latestVersion))
        {
            _logger.LogWarning("Failed to parse release tag `{TagName}` as a version for release `{ReleaseName}`", latestRelease.TagName, latestRelease.Name);
            return null;
        }

        var isLatestNewerThanCurrent = latestVersion.CompareTo(currentVersion) > 0;
        if (!isLatestNewerThanCurrent)
        {
            _logger.LogInformation("Latest version on GitHub `{LatestVersion}` is older or the same as current `{CurrentVersion}`", latestVersion, currentVersion);
            return null;
        }

        _logger.LogInformation("Latest version on GitHub `{LatestVersion}` is newer than current version `{CurrentVersion}`", latestVersion, currentVersion);
        return latestRelease;
    }

    public bool TryGetMatchingReleaseAsset(Release release, InstallationMethod installationMethod, [NotNullWhen(true)] out Asset? releaseAsset)
    {
        if (release.Assets.Count == 0)
        {
            releaseAsset = null;
            return false;
        }

        foreach (var asset in release.Assets)
        {
            if (!Matches(asset.Name, installationMethod)) continue;

            releaseAsset = asset;
            return true;
        }

        releaseAsset = null;
        return false;
    }

    private static readonly Extension ArchiveExtension = new(".zip");
    private static readonly Extension AppImageExtension = new(".AppImage");

    internal static bool Matches(RelativePath fileName, InstallationMethod installationMethod)
    {
        if (installationMethod is InstallationMethod.AppImage) return fileName.Extension.Equals(AppImageExtension);

        if (installationMethod is InstallationMethod.Archive)
        {
            if (!fileName.Extension.Equals(ArchiveExtension)) return false;
            return fileName.Path.Contains(".linux-", StringComparison.OrdinalIgnoreCase);
        }

        // not supported
        return false;
    }
}
