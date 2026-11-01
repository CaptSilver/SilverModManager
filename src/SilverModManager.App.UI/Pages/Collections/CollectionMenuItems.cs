using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>More-menu items more than one kind of collection offers.</summary>
public static class CollectionMenuItems
{
    /// <summary>
    /// Clones the collection into an editable local one and opens it in place of the page the item sits on.
    /// </summary>
    public static IContextMenuItem MakeEditableCopy(IServiceProvider services, CollectionGroup.ReadOnly group, ICollectionPageHost host)
    {
        var loadoutManager = services.GetRequiredService<ILoadoutManager>();
        var groupName = group.AsLoadoutItemGroup().AsLoadoutItem().Name;
        var loadoutId = group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId;

        return new ContextMenuItem
        {
            Header = Language.CollectionLoadoutView_MakeEditableLocalCopy,
            Icon = IconValues.Copy,
            Command = ReactiveCommand.CreateFromTask(async () =>
            {
                var dialog = DialogFactory.CreateStandardDialog(
                    "Collection Name",
                    new StandardDialogParameters
                    {
                        Text = "This is the name of the new cloned collection.",
                        InputLabel = "Collection name",
                        InputWatermark = "(Local) " + groupName,
                        InputText = "(Local) " + groupName,
                    },
                    [
                        DialogStandardButtons.Cancel,
                        new DialogButtonDefinition("Create", ButtonDefinitionId.Accept, ButtonAction.Accept, ButtonStyling.Primary),
                    ]
                );
                var result = await host.WindowManager.ShowDialog(dialog, DialogWindowType.Modal);
                if (result.ButtonId != ButtonDefinitionId.Accept || string.IsNullOrWhiteSpace(result.InputText))
                    return;

                var cloneId = await CollectionCreator.MakeEditableLocalCollection(loadoutManager, group.Db.Connection, group.Id, result.InputText);

                var pageData = new PageData
                {
                    FactoryId = LoadoutPageFactory.StaticId,
                    Context = new LoadoutPageContext
                    {
                        LoadoutId = loadoutId,
                        GroupScope = CollectionGroupId.From(cloneId),
                    },
                };

                var workspaceController = host.WorkspaceController;
                var behavior = workspaceController.GetOpenPageBehavior(pageData, NavigationInformation.From(OpenPageBehaviorType.ReplaceTab));
                workspaceController.OpenPage(host.WorkspaceId, pageData, behavior);
            }),
        };
    }
}
