using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Builds the <see cref="StagingPromoter"/> for an installation. A promoter works on one
/// installation's staging directory, so the container holds this factory rather than a promoter.
/// </summary>
public sealed class StagingPromoterFactory(IPromotionSink sink, IFileHasher hasher, ISettingsManager settings, ILoggerFactory loggerFactory, IFileSystem fileSystem)
{
    public StagingPromoter For(GameInstallMetadataId installation)
    {
        var vfsSettings = settings.Get<VfsSettings>();
        var staging = new StagingDirectory(vfsSettings.StagingLocation.ToPath(fileSystem), installation);
        return new StagingPromoter(staging, sink, hasher, loggerFactory.CreateLogger<StagingPromoter>(), Size.From(vfsSettings.PromotionSizeCapBytes));
    }
}
