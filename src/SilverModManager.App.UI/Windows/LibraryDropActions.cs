using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Controls.Spine;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Windows;

/// <summary>The drop actions of the real window: the library's add-local-file job and the workspace's pages.</summary>
internal sealed class LibraryDropActions(ILibraryService libraryService, IConnection connection, IWorkspaceController workspaceController) : IMainWindowDropActions
{
    public async Task<LibraryFileId> AddModlist(AbsolutePath file, CancellationToken cancellationToken)
    {
        var localFile = await libraryService.AddLocalFile(file);
        var id = LibraryFileId.From(localFile.Id);

        // The recogniser only marks a file that holds a readable modlist.
        if (!WabbajackModlistLibraryFile.Load(connection.Db, id).IsValid())
            throw new InvalidOperationException("it was added to the library, but it does not hold a modlist this version can read");
        return id;
    }

    public async Task AddMod(AbsolutePath file, CancellationToken cancellationToken) => await libraryService.AddLocalFile(file);

    public void OpenModlist(LibraryFileId file)
    {
        var page = new PageData
        {
            FactoryId = WabbajackModlistPageFactory.StaticId,
            Context = new WabbajackModlistPageContext(file),
        };
        workspaceController.OpenPage(workspaceController.ActiveWorkspaceId, page, workspaceController.GetDefaultOpenPageBehavior(page, NavigationInput.Default));
    }

    public string? CurrentLoadoutName
    {
        get
        {
            if (workspaceController.ActiveWorkspace.Context is not LoadoutContext context) return null;
            var loadout = Loadout.Load(connection.Db, context.LoadoutId);
            return loadout.IsValid() ? LoadoutLabel.For(loadout, connection.Db) : null;
        }
    }
}
