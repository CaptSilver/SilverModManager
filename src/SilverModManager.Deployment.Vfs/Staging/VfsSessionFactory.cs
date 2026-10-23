using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Puts a <see cref="VfsSession"/> around a built tree: the staging directory comes from
/// <see cref="VfsSettings.StagingLocation"/>, and the promoter from <see cref="StagingPromoterFactory"/>,
/// which applies <see cref="VfsSettings.PromotionSizeCapBytes"/>.
/// </summary>
/// <remarks>
/// The factory does not work out which loadout the game's writes belong to. The caller names it: the launch
/// that mounts the tree knows the loadout it synchronized just before mounting.
/// </remarks>
public sealed class VfsSessionFactory(StagingPromoterFactory promoters, ISettingsManager settings, IFileSystem fileSystem)
{
    /// <param name="installation">The installation whose game will write through the tree.</param>
    /// <param name="target">The loadout that receives what the game wrote when the session ends.</param>
    /// <param name="tree">The tree built with the staging directory for <paramref name="installation"/>.</param>
    public VfsSession Create(GameInstallMetadataId installation, LoadoutId target, VfsTree tree)
    {
        var staging = new StagingDirectory(settings.Get<VfsSettings>().StagingLocation.ToPath(fileSystem), installation);
        return new VfsSession(tree, staging, promoters.For(installation), target);
    }
}
