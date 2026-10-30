using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Wabbajack;

[JsonName("SilverModManager.App.UI.Pages.Wabbajack.WabbajackModlistPageContext")]
public record WabbajackModlistPageContext : IPageFactoryContext
{
    public required LibraryFileId LibraryFileId { get; init; }

    /// <summary>The loadout the page starts on instead of the first one of the list's game.</summary>
    public LoadoutId? TargetLoadout { get; init; }
}

[UsedImplicitly]
public class WabbajackModlistPageFactory : APageFactory<IWabbajackModlistViewModel, WabbajackModlistPageContext>
{
    public WabbajackModlistPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("7d2f5c1e-48a3-4b6d-9e07-c3a15b8f2e64"));
    public override PageFactoryId Id => StaticId;

    public override IWabbajackModlistViewModel CreateViewModel(WabbajackModlistPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IWabbajackModlistViewModel>();
        vm.FileId = context.LibraryFileId;
        vm.PreferredLoadout = context.TargetLoadout;
        return vm;
    }

    // Nothing offers the page from a menu: it opens from a modlist's row in the Library.
    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext) => [];
}
