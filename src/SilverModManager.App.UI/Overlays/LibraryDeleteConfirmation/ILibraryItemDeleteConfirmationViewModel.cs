using SilverModManager.App.UI.Pages.Library;
namespace SilverModManager.App.UI.Overlays.LibraryDeleteConfirmation;

/// <summary>
///     Interface for a view model for the confirmation dialog displayed
///     when a dangerous delete operation is about to be performed.
/// </summary>
public interface ILibraryItemDeleteConfirmationViewModel : IOverlayViewModel<bool>
{
    /// <summary>
    /// Names of all items that are about to be removed.
    /// </summary>
    public List<string> AllItems { get; }
    
    /// <summary>
    /// Listing of all loadouts that contain the mods listed in <see cref="AllItems"/>.
    /// </summary>
    public List<LibraryItemUsedLoadoutInfo> LoadoutsUsed { get; }

    /// <summary>
    /// Collections that own their mods and link the items being deleted; they keep their mods.
    /// </summary>
    public List<LibraryItemKeptByCollectionInfo> KeptByCollections { get; }
}

