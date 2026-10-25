using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Pages.NexusCatalog;

[JsonName("SilverModManager.App.UI.Pages.NexusCatalog.NexusModsPageContext")]
public record NexusModsPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class NexusModsPageFactory : APageFactory<INexusModsViewModel, NexusModsPageContext>
{
    private readonly IConnection _connection;

    public NexusModsPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("5d0a7e19-3c84-4b62-8f15-a9e2c6d17b40"));
    public override PageFactoryId Id => StaticId;

    public override INexusModsViewModel CreateViewModel(NexusModsPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<INexusModsViewModel>();
        vm.LoadoutId = context.LoadoutId;
        return vm;
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        // By the recorded Nexus id, so the page doesn't depend on the game still being located.
        var loadout = Loadout.Load(_connection.Db, loadoutContext.LoadoutId);
        if (!GameInstallMetadata.GameId.TryGetValue(loadout.Installation, out _)) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Mods",
            ItemName = "Nexus Mods",
            Icon = IconValues.Download,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new NexusModsPageContext { LoadoutId = loadoutContext.LoadoutId },
            },
        };
    }
}
