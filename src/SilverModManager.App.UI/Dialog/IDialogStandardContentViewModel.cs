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

    /// <summary>
    /// What the copy button puts on the clipboard in place of the markdown, for details that are meant to be
    /// pasted into a bug report but not read in the dialog. Never drawn.
    /// </summary>
    string? CopyText { get; }
    string InputText { get; set; }
    string InputLabel { get; set; }
    string InputWatermark { get; set; }
    string BottomText { get; }
    public R3.ReactiveCommand ClearInputCommand { get; set; }
}
