using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Games.FromSoft;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.FromSoftMerges;

[JsonName("SilverModManager.App.UI.Pages.FromSoftMerges.FromSoftMergesPageContext")]
public record FromSoftMergesPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class FromSoftMergesPageFactory : APageFactory<IFromSoftMergesViewModel, FromSoftMergesPageContext>
{
    private readonly IConnection _conn;

    public FromSoftMergesPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("6c2e9a41-3d57-4b80-9e14-a7f05b3c8d62"));
    public override PageFactoryId Id => StaticId;

    public override IFromSoftMergesViewModel CreateViewModel(FromSoftMergesPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IFromSoftMergesViewModel>();
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
            ItemName = "Merges",
            Icon = IconValues.Swap,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new FromSoftMergesPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
