using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.About;

[JsonName("SilverModManager.App.UI.Pages.About.AboutPageContext")]
public record AboutPageContext : IPageFactoryContext;

[UsedImplicitly]
public class AboutPageFactory : APageFactory<IAboutPageViewModel, AboutPageContext>
{
    public AboutPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("6f0d7c2e-3b5a-4c1e-9a47-2d8e51b0c9f3"));
    public override PageFactoryId Id => StaticId;

    public override IAboutPageViewModel CreateViewModel(AboutPageContext context)
    {
        return ServiceProvider.GetRequiredService<IAboutPageViewModel>();
    }
}
