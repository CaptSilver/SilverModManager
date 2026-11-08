using ReactiveUI;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>The more-menu entry every kind of collection shows when an update is known.</summary>
public static class CollectionUpdateMenuItems
{
    /// <summary>"Update to {version}…": starts the review and, if the user accepts, the update.</summary>
    public static IContextMenuItem UpdateTo(IServiceProvider services, ICollectionUpdate update, ICollectionPageHost host) => new ContextMenuItem
    {
        Header = string.Format(Language.CollectionUpdate_Menu_UpdateTo, update.NewVersionText),
        Icon = IconValues.Update,
        Command = ReactiveCommand.CreateFromTask(() => CollectionUpdateFlow.Run(
            services,
            host.WindowManager,
            services.GetRequiredService<IWindowNotificationService>(),
            update,
            CancellationToken.None)),
    };
}
