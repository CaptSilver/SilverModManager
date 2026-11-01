using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>What the Library page and the drag targets agree on about installing library items as mods.</summary>
internal static class CollectionInstallRules
{
    // A modlist row still carries its library item so it can be selected and deleted, but installing the
    // .wabbajack itself as a mod would put the zip's manifest into the loadout. The list's page installs it.
    internal static bool IsInstallableAsMod(LibraryItem.ReadOnly item)
        => item.TryGetAsLibraryFile(out var file) is false || file.TryGetAsWabbajackModlistLibraryFile(out _) is false;
}
