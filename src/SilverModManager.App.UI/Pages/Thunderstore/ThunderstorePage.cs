using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Thunderstore;

[JsonName("SilverModManager.App.UI.Pages.Thunderstore.ThunderstorePageContext")]
public record ThunderstorePageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class ThunderstorePageFactory : APageFactory<IThunderstoreViewModel, ThunderstorePageContext>
{
    private readonly IConnection _connection;

    public ThunderstorePageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("b3c2d8a4-6e51-4f07-9a1d-72c5e0f48b96"));
    public override PageFactoryId Id => StaticId;

    public override IThunderstoreViewModel CreateViewModel(ThunderstorePageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IThunderstoreViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        // By the recorded game id, like the library data provider: the page should not depend on the game still being located.
        var loadout = Loadout.Load(_connection.Db, loadoutContext.LoadoutId);
        if (!loadout.TryGetGame(out var game) || ThunderstoreGame.For(game) is null) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Mods",
            ItemName = "Thunderstore",
            Icon = IconValues.Download,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new ThunderstorePageContext
                {
                    LoadoutId = loadoutContext.LoadoutId,
                },
            },
        };
    }
}
