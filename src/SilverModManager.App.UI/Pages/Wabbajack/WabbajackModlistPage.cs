using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.App.UI.Pages.Wabbajack;

[JsonName("SilverModManager.App.UI.Pages.Wabbajack.WabbajackModlistPageContext")]
public record WabbajackModlistPageContext : IPageFactoryContext
{
    public required LibraryFileId LibraryFileId { get; init; }

    /// <summary>The loadout the page is fixed to. Without one the page lets the user pick a loadout of the list's game.</summary>
    public LoadoutId? TargetLoadout { get; init; }
}

[UsedImplicitly]
public class WabbajackModlistPageFactory : APageFactory<ICollectionLoadoutViewModel, WabbajackModlistPageContext>
{
    private readonly IConnection _connection;

    public WabbajackModlistPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("7d2f5c1e-48a3-4b6d-9e07-c3a15b8f2e64"));
    public override PageFactoryId Id => StaticId;

    public override ICollectionLoadoutViewModel CreateViewModel(WabbajackModlistPageContext context)
    {
        // A saved workspace can hold a tab for a list or a loadout the user has since removed.
        var file = WabbajackModlistLibraryFile.Load(_connection.Db, context.LibraryFileId.Value);
        if (file.IsValid() is false)
            throw new PageTargetGoneException($"Modlist {context.LibraryFileId} is no longer in the library");
        if (context.TargetLoadout is { } loadout && Loadout.Load(_connection.Db, loadout.Value).IsValid() is false)
            throw new PageTargetGoneException($"Loadout {loadout} no longer exists");

        return new CollectionLoadoutViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(),
            ServiceProvider,
            new WabbajackModlistSource(ServiceProvider, context.TargetLoadout, file));
    }

    // The loadout's left menu offers a modlist, and the Library, the gallery and a dropped file open it.
    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext) => [];
}
