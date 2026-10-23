using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

[JsonName("SilverModManager.App.UI.Page.ApplyDiffPageContext")]
public record ApplyDiffPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class ApplyDiffPageFactory : APageFactory<IApplyDiffViewModel, ApplyDiffPageContext>
{
    private readonly IConnection _connection;
    public ApplyDiffPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("CA9E8FFE-02E0-4123-9CA2-F68D07D44583"));
    public override PageFactoryId Id => StaticId;

    public override IApplyDiffViewModel CreateViewModel(ApplyDiffPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IApplyDiffViewModel>();
        vm.Initialize(context.LoadoutId);
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_connection.Db, loadoutContext.LoadoutId);

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Preview Apply Changes",
            Icon = IconValues.ListFilled,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new ApplyDiffPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }

}
