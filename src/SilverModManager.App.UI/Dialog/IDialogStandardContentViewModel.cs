using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.Dialog;

public interface IDialogStandardContentViewModel : IViewModelInterface
{
    string Text { get; }
    string Heading { get; }
    IconValue? Icon { get; }
    IMarkdownRendererViewModel? MarkdownRenderer { get; }
    bool ShowMarkdownCopyButton { get; }
    string InputText { get; set; }
    string InputLabel { get; set; }
    string InputWatermark { get; set; }
    string BottomText { get; }
    public R3.ReactiveCommand ClearInputCommand { get; set; }
}
