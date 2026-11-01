using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Represents an item in the library.
/// </summary>
[PublicAPI]
public partial class LibraryItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.LibraryItem";

    /// <summary>
    /// Name of the library item.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>The games this item is for. Several when one file serves more than one game.</summary>
    public static readonly GameIdsAttribute DestinationGames = new(Namespace, nameof(DestinationGames)) { IsIndexed = true };

    /// <summary>
    /// Set when the games were assigned because Nexus Mods does not tell them apart (Skyrim SE and Skyrim VR
    /// share one Nexus id), not because the item was acquired or installed for each of them.
    /// </summary>
    public static readonly MarkerAttribute DestinationsShared = new(Namespace, nameof(DestinationsShared)) { IsIndexed = true };

    /// <summary>
    /// Set when no source, loadout or modlist says what game the item is for. Cleared when a game is added.
    /// Indexed so the unassigned items are a slice too, not a scan for absence.
    /// </summary>
    public static readonly MarkerAttribute DestinationUnknown = new(Namespace, nameof(DestinationUnknown)) { IsIndexed = true };
}

public partial class LibraryItem
{
    [PublicAPI]
    public partial struct ReadOnly
    {
        /// <summary>
        /// Adds a retraction which effectively deletes the current archived file from the data store.
        /// </summary>
        /// <param name="tx">The transaction to add the retraction to.</param>
        public void Retract(ITransaction tx) => tx.Retract<string, StringAttribute>(Id, LibraryItem.Name, Name);
        
        /// <summary>
        /// Tries to get the entity as a DownloadedFile entity, if the entity is not a DownloadedFile entity, it returns false.
        /// </summary>
        public bool TryGetAsDownloadedFile(out DownloadedFile.ReadOnly result) {
            // This is same as source generator would make, just that we don't
            // currently don't autogenerate conversions for multiple levels of include.
            var casted = new DownloadedFile.ReadOnly(Db, EntitySegment, Id);
            if (casted.IsValid()) {
                result = casted;
                return true;
            }

            result = default(DownloadedFile.ReadOnly);
            return false;
        }
        
        /// <summary>
        /// Tries to get the entity as a LocalFile entity, if the entity is not a LocalFile entity, it returns false.
        /// </summary>
        public bool TryGetAsLocalFile(out LocalFile.ReadOnly result) {
            // This is same as source generator would make, just that we don't
            // currently don't autogenerate conversions for multiple levels of include.
            var casted = new LocalFile.ReadOnly(Db, EntitySegment, Id);
            if (casted.IsValid()) {
                result = casted;
                return true;
            }

            result = default(LocalFile.ReadOnly);
            return false;
        }
    }
}
