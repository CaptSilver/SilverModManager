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
    /// <param name="root">The mounted folder; what the game wrote is recorded under it.</param>
    public StagingPromoter For(GameInstallMetadataId installation, GamePath root)
    {
        var vfsSettings = settings.Get<VfsSettings>();
        var staging = new StagingDirectory(vfsSettings.StagingLocation.ToPath(fileSystem), installation);
        return new StagingPromoter(staging, root, sink, hasher, loggerFactory.CreateLogger<StagingPromoter>(), Size.From(vfsSettings.PromotionSizeCapBytes));
    }
}
