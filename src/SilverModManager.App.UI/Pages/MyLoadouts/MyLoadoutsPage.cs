using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

[JsonName("SilverModManager.App.UI.Pages.MyLoadoutsPageContext")]
public record MyLoadoutsPageContext : IPageFactoryContext
{
    /// <summary>
    /// When set, the page lists only this game installation's loadouts. Layouts saved before the filter existed
    /// have no value here and show every game.
    /// </summary>
    public GameInstallMetadataId? InstallationId { get; init; }
}

[UsedImplicitly]
public class MyLoadoutsPageFactory : APageFactory<IMyLoadoutsViewModel, MyLoadoutsPageContext>
{
    public MyLoadoutsPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("AF0C54D6-04AE-4F80-812E-1DB31A599C58"));
    public override PageFactoryId Id => StaticId;

    public override IMyLoadoutsViewModel CreateViewModel(MyLoadoutsPageContext context)
    {
        return new MyLoadoutsViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(),
            ServiceProvider.GetRequiredService<IConnection>(),
            ServiceProvider,
            context.InstallationId
        );
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not HomeContext) yield break;

        yield return new PageDiscoveryDetails
        {
            // TODO: Update with design mandate names
            SectionName = "Loadouts",
            ItemName = Language.MyLoadoutsPageTitle,
            Icon = IconValues.Package,
            PageData = new PageData
            {
                FactoryId = Id,
                Context = new MyLoadoutsPageContext(),
            },
        };
    }
}
