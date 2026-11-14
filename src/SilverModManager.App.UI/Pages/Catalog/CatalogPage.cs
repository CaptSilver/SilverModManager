using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Catalog;

/// <summary>
/// Which catalog's page to open, for which loadout. A tab saved before the page served every catalog has no
/// <see cref="Catalog"/>, and those tabs were all Thunderstore's, so that is the default; the serialized name and the
/// factory id are the earlier page's for the same reason.
/// </summary>
[JsonName("SilverModManager.App.UI.Pages.Thunderstore.ThunderstorePageContext")]
public record CatalogPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }

    /// <summary>The <see cref="IModCatalog.DisplayName"/> of the catalog the page lists.</summary>
    public string Catalog { get; init; } = "Thunderstore";
}

[UsedImplicitly]
public class CatalogPageFactory : APageFactory<ICatalogViewModel, CatalogPageContext>
{
    private readonly IConnection _connection;
    private readonly ModCatalogs _catalogs;

    public CatalogPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _catalogs = serviceProvider.GetRequiredService<ModCatalogs>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("b3c2d8a4-6e51-4f07-9a1d-72c5e0f48b96"));
    public override PageFactoryId Id => StaticId;

    public override ICatalogViewModel CreateViewModel(CatalogPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<ICatalogViewModel>();
        vm.Initialize(context.Catalog, context.LoadoutId);
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        // By the recorded game id, like the library data provider: the page should not depend on the game still being located.
        var loadout = Loadout.Load(_connection.Db, loadoutContext.LoadoutId);
        if (!loadout.TryGetGame(out var game)) yield break;

        // Nexus Mods keeps its own page, which also lists collections.
        foreach (var catalog in CatalogLeftMenuItems.Browsable(_catalogs, game))
        {
            yield return new PageDiscoveryDetails
            {
                SectionName = "Mods",
                ItemName = catalog.DisplayName,
                Icon = IconValues.Download,
                PageData = new PageData
                {
                    FactoryId = StaticId,
                    Context = new CatalogPageContext { LoadoutId = loadoutContext.LoadoutId, Catalog = catalog.DisplayName },
                },
            };
        }
    }
}
