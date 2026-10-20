using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Loadouts;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Tools;

[JsonName("SilverModManager.App.UI.Pages.Tools.ToolsPageContext")]
public record ToolsPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class ToolsPageFactory : APageFactory<IToolsPageViewModel, ToolsPageContext>
{
    private readonly IConnection _conn;

    public ToolsPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("3c9a5e71-8b24-4d06-a1f3-7e52c0b94d18"));
    public override PageFactoryId Id => StaticId;

    public override IToolsPageViewModel CreateViewModel(ToolsPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IToolsPageViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = "Tools",
            Icon = IconValues.Wrench,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new ToolsPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
