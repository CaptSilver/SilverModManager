using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// A mod another manager (Vortex) deployed into the game folder before the game was managed here. Its files are
/// the loadout's own copies of what was on disk, so switching the group off removes them from the game folder and
/// switching it on writes them back.
/// </summary>
/// <remarks>
/// The attributes keep where the mod came from, so a later update check or Nexus link can use them without
/// asking the other manager again.
/// </remarks>
[Include<LoadoutItemGroup>]
public partial class ForeignDeploymentGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.ForeignDeploymentGroup";

    /// <summary>The manager that deployed the mod, as the player knows it ("Vortex").</summary>
    public static readonly StringAttribute Manager = new(Namespace, nameof(Manager));

    /// <summary>
    /// The manager's own name for the mod, whole. For Vortex that is the staging folder name,
    /// <c>name-modId-version-time</c>, or <c>Bundled - ...</c> for files a collection bundled.
    /// </summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source));

    /// <summary>The Nexus Mods id read from <see cref="Source"/>, when it carries one.</summary>
    public static readonly ModIdAttribute NexusModId = new(Namespace, nameof(NexusModId)) { IsOptional = true };

    /// <summary>The version text read from <see cref="Source"/>, when it carries one.</summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };
}
