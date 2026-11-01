using SilverModManager.Sdk.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>What a drag carries. A drop target reads the kind it understands and ignores the rest.</summary>
public abstract record DragPayload
{
    /// <summary>Paths from the OS. Files, folders, anything; the target classifies.</summary>
    public sealed record DroppedFiles(IReadOnlyList<string> Paths) : DragPayload;

    /// <summary>Rows of the Library page of <paramref name="Loadout"/>.</summary>
    public sealed record LibraryItems(LoadoutId Loadout, IReadOnlyList<LibraryItemId> Items) : DragPayload;

    /// <summary>
    /// Loadout items from the rows of a loadout page of <paramref name="Loadout"/>, as the rows carry them. Which are
    /// mods, and which collection each sits in, is read from the database at drop time.
    /// </summary>
    public sealed record LoadoutMods(LoadoutId Loadout, IReadOnlyList<LoadoutItemId> Items) : DragPayload;
}
