using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.Merges;

// The name is a persisted layout key: saved workspaces store it, so it keeps the namespace and type this page was
// first shipped under.
[JsonName("SilverModManager.App.UI.Pages.FromSoftMerges.FromSoftMergesPageContext")]
public record MergesPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class MergesPageFactory : APageFactory<IMergesViewModel, MergesPageContext>
{
    private readonly IConnection _conn;
    private readonly MergeEngines _engines;

    public MergesPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _conn = serviceProvider.GetRequiredService<IConnection>();
        _engines = serviceProvider.GetRequiredService<MergeEngines>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("6c2e9a41-3d57-4b80-9e14-a7f05b3c8d62"));
    public override PageFactoryId Id => StaticId;

    public override IMergesViewModel CreateViewModel(MergesPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IMergesViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);
        if (_engines.For(loadout.InstallationInstance.Game) is null) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Merges",
            Icon = IconValues.Swap,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new MergesPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
