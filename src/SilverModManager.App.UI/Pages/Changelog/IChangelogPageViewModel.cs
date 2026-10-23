using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Pages.Changelog;

public interface IChangelogPageViewModel : IPageViewModelInterface
{
    public Version? TargetVersion { get; set; }

    public ParsedChangelog? ParsedChangelog { get; }

    public int SelectedIndex { get; set; }

    public IMarkdownRendererViewModel MarkdownRendererViewModel { get; }
}
