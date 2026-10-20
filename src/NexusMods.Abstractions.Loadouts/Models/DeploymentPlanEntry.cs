using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// One root of a game installation's deployment plan and the backend that deploys it.
/// </summary>
public partial class DeploymentPlanEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.DeploymentPlanEntry";

    /// <summary>
    /// The owning game installation
    /// </summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation)) { IsIndexed = true };

    /// <summary>
    /// The root the backend is responsible for. The parent is the installation id, same shape as
    /// <see cref="DiskStateEntry.Path"/>.
    /// </summary>
    public static readonly GamePathParentAttribute Root = new(Namespace, nameof(Root));

    /// <summary>
    /// The id of the backend, <c>DeploymentBackendId.Value</c>
    /// </summary>
    public static readonly StringAttribute Backend = new(Namespace, nameof(Backend));
}
