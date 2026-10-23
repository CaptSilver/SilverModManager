using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.About;

public interface IAboutPageViewModel : IPageViewModelInterface
{
    public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }
}
