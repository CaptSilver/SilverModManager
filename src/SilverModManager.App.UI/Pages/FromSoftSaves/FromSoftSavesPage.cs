using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Games.FromSoft;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.FromSoftSaves;

[JsonName("SilverModManager.App.UI.Pages.FromSoftSaves.FromSoftSavesPageContext")]
public record FromSoftSavesPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class FromSoftSavesPageFactory : APageFactory<IFromSoftSavesViewModel, FromSoftSavesPageContext>
{
    private readonly IConnection _conn;

    public FromSoftSavesPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("0f4b7d52-9c1e-4a38-b6d0-5e8a2c71f394"));
    public override PageFactoryId Id => StaticId;

    public override IFromSoftSavesViewModel CreateViewModel(FromSoftSavesPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IFromSoftSavesViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);
        if (loadout.InstallationInstance.Game is not IFromSoftGame) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Saves",
            Icon = IconValues.Save,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new FromSoftSavesPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
