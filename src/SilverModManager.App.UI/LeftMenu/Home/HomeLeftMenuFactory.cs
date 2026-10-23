using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.LeftMenu.Home;

public class HomeLeftMenuFactory(IServiceProvider serviceProvider) : ILeftMenuFactory<HomeContext>
{
    public ILeftMenuViewModel CreateLeftMenuViewModel(HomeContext context, WorkspaceId workspaceId,
        IWorkspaceController workspaceController)
    {
        return new HomeLeftMenuViewModel(serviceProvider.GetRequiredService<IMyGamesViewModel>(), workspaceId,
            workspaceController);
    }
}
