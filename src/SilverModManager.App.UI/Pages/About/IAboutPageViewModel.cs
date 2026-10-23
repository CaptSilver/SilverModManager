using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.About;

public interface IAboutPageViewModel : IPageViewModelInterface
{
    public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }
}
