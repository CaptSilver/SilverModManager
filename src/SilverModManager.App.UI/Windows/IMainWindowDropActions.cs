using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Windows;

/// <summary>What dropped files do in the app. Everything goes through the library; nothing is copied into a game folder.</summary>
public interface IMainWindowDropActions
{
    /// <summary>Adds a <c>.wabbajack</c> file with the library's add-local-file job. Throws if the library did not recognise it as a modlist.</summary>
    Task<LibraryFileId> AddModlist(AbsolutePath file, CancellationToken cancellationToken);

    /// <summary>Adds a mod archive with the library's add-local-file job.</summary>
    Task AddMod(AbsolutePath file, CancellationToken cancellationToken);

    void OpenModlist(LibraryFileId file);

    /// <summary>The loadout of the page that is open, or null when the window is not on a loadout.</summary>
    string? CurrentLoadoutName { get; }
}
