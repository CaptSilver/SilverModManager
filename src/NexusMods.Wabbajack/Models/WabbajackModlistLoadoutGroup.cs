using JetBrains.Annotations;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Wabbajack.Models;

/// <summary>
/// The group a modlist's mods are installed under. It includes <see cref="CollectionGroup"/> because the synchronizer
/// only honours a parent's <see cref="LoadoutItem.Disabled"/> when the parent is a collection group, and the list stays
/// disabled until every directive was executed. There is deliberately no fallback to a plain group.
/// </summary>
[PublicAPI]
[Include<CollectionGroup>]
public partial class WabbajackModlistLoadoutGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.ModlistLoadoutGroup";

    /// <summary>The library file the list was installed from.</summary>
    public static readonly ReferenceAttribute<WabbajackModlistLibraryFile> Modlist = new(Namespace, nameof(Modlist)) { IsIndexed = true };

    /// <summary>When the install finished ordering the mods. Absent while the install is incomplete.</summary>
    public static readonly TimestampAttribute InstalledAt = new(Namespace, nameof(InstalledAt)) { IsOptional = true };
}
