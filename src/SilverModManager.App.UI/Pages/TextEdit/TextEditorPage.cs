using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.WorkspaceSystem;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using OneOf;

namespace SilverModManager.App.UI.Pages.TextEdit;

[JsonName("TextEditorPageContext")]
public record TextEditorPageContext : IPageFactoryContext
{
    public required OneOf<LoadoutFileId, LibraryFileId> FileId { get; init; }
    public required RelativePath FilePath { get; init; }
    public required bool IsReadOnly { get; init; }
}

[UsedImplicitly]
public class TextEditorPageFactory : APageFactory<ITextEditorPageViewModel, TextEditorPageContext>
{
    public TextEditorPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("ea4947ea-5f97-4e4d-abf2-bd511fbd9b4e"));
    public override PageFactoryId Id => StaticId;

    public override ITextEditorPageViewModel CreateViewModel(TextEditorPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<ITextEditorPageViewModel>();
        vm.Context = context;
        return vm;
    }
}
