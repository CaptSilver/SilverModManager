using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

[JsonName("SilverModManager.App.UI.Pages.ViewLoadoutGroupFilesPageContext")]
public record LoadoutGroupFilesPageContext : IPageFactoryContext
{
    public required LoadoutItemGroupId[] GroupIds { get; init; }
    
    public required bool IsReadOnly { get; init; }
}

[UsedImplicitly]
public class LoadoutGroupFilesPageFactory : APageFactory<ILoadoutGroupFilesViewModel, LoadoutGroupFilesPageContext>
{
    public LoadoutGroupFilesPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("f11ed47a-c0de-4b0b-8eef-a5513c0d3d4a"));
    public override PageFactoryId Id => StaticId;

    public override ILoadoutGroupFilesViewModel CreateViewModel(LoadoutGroupFilesPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<ILoadoutGroupFilesViewModel>();
        vm.Context = context;
        return vm;
    }
}
