using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.WorkspaceAttachments;

public class LoadoutAttachmentsFactory(IConnection conn) : IWorkspaceAttachmentsFactory<LoadoutContext>
{
    public string CreateTitle(LoadoutContext context)
    {
        // Use the game name as the title
        var loadout = Loadout.Load(conn.Db, context.LoadoutId);
        return loadout.InstallationInstance.Game.DisplayName;
    }

    public string CreateSubtitle(LoadoutContext context)
    {
        // Use the loadout name as the subtitle
        var loadout = Loadout.Load(conn.Db, context.LoadoutId);
        return loadout.Name;
    }
}
