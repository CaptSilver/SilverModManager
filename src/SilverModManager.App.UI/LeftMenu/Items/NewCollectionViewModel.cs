using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Items;

public class NewCollectionViewModel : LeftMenuItemViewModel
{
    public NewCollectionViewModel(
        IServiceProvider serviceProvider,
        LoadoutId loadoutId,
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId) : base(workspaceController, workspaceId, null!)
    {
        Text = new StringComponent(value: "New Collection");
        Icon = IconValues.Add;

        var connection = serviceProvider.GetRequiredService<IConnection>();

        NavigateCommand = ReactiveCommand.CreateFromTask<NavigationInformation>(async (navigationInfo, _) =>
        {
            var dialog = LoadoutDialogs.CreateCollection();
            var windowManager = serviceProvider.GetRequiredService<IWindowManager>();
            var result = await windowManager.ShowDialog(dialog, DialogWindowType.Modal);
            if (result.ButtonId != ButtonDefinitionId.Accept) return;

            var collectionGroup = await CollectionCreator.CreateNewCollectionGroup(connection, loadoutId, newName: result.InputText);

            var pageData = new PageData
            {
                FactoryId = LoadoutPageFactory.StaticId,
                Context = new LoadoutPageContext
                {
                    LoadoutId = loadoutId,
                    GroupScope = collectionGroup.CollectionGroupId,
                },
            };

            var behavior = workspaceController.GetOpenPageBehavior(pageData, navigationInfo);
            workspaceController.OpenPage(workspaceId, pageData, behavior);
        });
    }
}
