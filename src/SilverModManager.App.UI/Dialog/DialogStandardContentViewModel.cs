using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI.SourceGenerators;
using ReactiveCommand = R3.ReactiveCommand;

namespace SilverModManager.App.UI.Dialog;

public partial class DialogStandardContentViewModel : AViewModel<IDialogStandardContentViewModel>, IDialogStandardContentViewModel
{
    public string Text { get; }
    public string Heading { get; }
    public IconValue? Icon { get; }
    public IMarkdownRendererViewModel? MarkdownRenderer { get; }
    public bool ShowMarkdownCopyButton { get; }
    public string? CopyText { get; }
    [Reactive] public partial string InputText { get; set; }
    public string InputLabel { get; set; }
    public string InputWatermark { get; set; }
    public string BottomText { get; }
    public ReactiveCommand ClearInputCommand { get; set; }

    public DialogStandardContentViewModel(StandardDialogParameters standardDialogParameters)
    {
        Text = standardDialogParameters.Text;
        Heading = standardDialogParameters.Heading;
        Icon = standardDialogParameters.Icon;
        MarkdownRenderer = standardDialogParameters.Markdown;
        InputLabel = standardDialogParameters.InputLabel;
        InputText = standardDialogParameters.InputText;
        InputWatermark = standardDialogParameters.InputWatermark;
        BottomText = standardDialogParameters.BottomText;
        ShowMarkdownCopyButton = standardDialogParameters.ShowMarkdownCopyButton;
        CopyText = standardDialogParameters.CopyText;

        ClearInputCommand = new R3.ReactiveCommand(
            executeAsync: (_, cancellationToken) =>
            {
                InputText = string.Empty;
                return ValueTask.CompletedTask;
            }
        );
    }
}
