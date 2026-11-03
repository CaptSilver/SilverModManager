using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Collections;

/// <summary>
/// A mod of an installed collection that an unattended install could not finish: it waits in the collection's review
/// list until someone installs it or dismisses it.
/// </summary>
public partial class NexusCollectionPendingInstall : IModelDefinition
{
    private const string Namespace = "SilverModManager.Collections.NexusCollectionPendingInstall";

    /// <summary>
    /// The collection group the mod belongs to.
    /// </summary>
    public static readonly ReferenceAttribute<NexusCollectionLoadoutGroup> CollectionGroup = new(Namespace, nameof(CollectionGroup)) { IsIndexed = true };

    /// <summary>
    /// The collection download that is waiting.
    /// </summary>
    public static readonly ReferenceAttribute<CollectionDownload> Download = new(Namespace, nameof(Download)) { IsIndexed = true };

    /// <summary>
    /// Why the mod is waiting.
    /// </summary>
    public static readonly EnumAttribute<ReviewKind> Kind = new(Namespace, nameof(Kind));

    /// <summary>
    /// The installer's explanation, if it gave one.
    /// </summary>
    public static readonly StringAttribute Reason = new(Namespace, nameof(Reason)) { IsOptional = true };

    /// <summary>
    /// The user chose to leave this optional mod alone.
    /// </summary>
    public static readonly BooleanAttribute Dismissed = new(Namespace, nameof(Dismissed)) { IsOptional = true };

    /// <summary>
    /// The entity id value of the older version's group this mod replaces once it installs.
    /// </summary>
    public static readonly UInt64Attribute ReplacesGroup = new(Namespace, nameof(ReplacesGroup)) { IsOptional = true };

    /// <summary>
    /// Whether the replaced group was disabled, so its replacement can be disabled too.
    /// </summary>
    public static readonly BooleanAttribute ReplacedWasDisabled = new(Namespace, nameof(ReplacedWasDisabled)) { IsOptional = true };
}
