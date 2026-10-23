using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.LibraryPage;

[JsonName("SilverModManager.App.UI.Pages.Library.LibraryPageContext")]
public record LibraryPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class LibraryPageFactory : APageFactory<ILibraryViewModel, LibraryPageContext>
{
    public LibraryPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("547926e3-56ba-4ed1-912d-d0d7e8b7e287"));
    public override PageFactoryId Id => StaticId;

    public override ILibraryViewModel CreateViewModel(LibraryPageContext context)
    {
        var vm = new LibraryViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(), 
            ServiceProvider,
            ServiceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            context.LoadoutId);
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Mods",
            ItemName = Language.LibraryPageTitle,
            Icon = IconValues.LibraryOutline,
            PageData = new PageData
            {
                FactoryId = Id,
                Context = new LibraryPageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                },
            },
        };
    }
}
