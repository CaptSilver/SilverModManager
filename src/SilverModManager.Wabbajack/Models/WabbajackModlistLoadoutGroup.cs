using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Wabbajack.Models;

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

    /// <summary>
    /// The folder under the extras root that holds the list's tools. Removing the group deletes it, so it is only ever
    /// acted on when it lies inside the extras root.
    /// </summary>
    public static readonly StringAttribute ExtrasFolder = new(Namespace, nameof(ExtrasFolder)) { IsOptional = true };

    /// <summary>
    /// The library file the group was installed from before an update switched it to <see cref="Modlist"/>. Set from the
    /// switch until the update is finished; with <see cref="InstalledAt"/> absent it marks an update in progress.
    /// </summary>
    public static readonly ReferenceAttribute<WabbajackModlistLibraryFile> PreviousModlist = new(Namespace, nameof(PreviousModlist)) { IsOptional = true, IsIndexed = true };

    /// <summary>The restore point written before the update switched the group, which Undo reverts to.</summary>
    public static readonly ReferenceAttribute<Transaction> LastUpdateRestorePoint = new(Namespace, nameof(LastUpdateRestorePoint)) { IsOptional = true };

    /// <summary>Whether the group was switched off by the user before the update, so finishing it can put that back.</summary>
    public static readonly BooleanAttribute WasDisabledByUser = new(Namespace, nameof(WasDisabledByUser)) { IsOptional = true };
}
