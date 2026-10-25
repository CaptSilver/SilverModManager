using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.App.UI.Pages.WabbajackGallery;

[JsonName("SilverModManager.App.UI.Pages.WabbajackGallery.WabbajackGalleryPageContext")]
public record WabbajackGalleryPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class WabbajackGalleryPageFactory : APageFactory<IWabbajackGalleryViewModel, WabbajackGalleryPageContext>
{
    private readonly IConnection _connection;

    public WabbajackGalleryPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("e4a17c58-2b93-4d06-8f3a-1c70b95d2e84"));
    public override PageFactoryId Id => StaticId;

    public override IWabbajackGalleryViewModel CreateViewModel(WabbajackGalleryPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IWabbajackGalleryViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        // By the recorded game id, like the other store pages: the page should not depend on the game still being located.
        var loadout = Loadout.Load(_connection.Db, loadoutContext.LoadoutId);
        if (!loadout.TryGetGame(out var game) || !GalleryGames.HasLists(game.GameId)) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Mods",
            ItemName = "Wabbajack",
            Icon = IconValues.Download,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new WabbajackGalleryPageContext { LoadoutId = loadoutContext.LoadoutId },
            },
        };
    }
}
