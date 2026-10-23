using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.MyLoadouts;

[JsonName("SilverModManager.App.UI.Pages.MyLoadoutsPageContext")]
public record MyLoadoutsPageContext : IPageFactoryContext;

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
        return ServiceProvider.GetRequiredService<IMyLoadoutsViewModel>();
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
