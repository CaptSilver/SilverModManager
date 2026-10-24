using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI.SourceGenerators;
using ReactiveCommand = R3.ReactiveCommand;

namespace SilverModManager.App.UI.Dialog;


public partial class DialogStandardContentDesignViewModel : AViewModel<IDialogStandardContentViewModel>, IDialogStandardContentViewModel
{
    public string Text { get; } = "Sample text for design-time view.";
    public string Heading { get; } = "Sample Heading";
    public IconValue? Icon { get; } = IconValues.Cog;
    public IMarkdownRendererViewModel? MarkdownRenderer { get; }
    public bool ShowMarkdownCopyButton { get; } = true;
    [Reactive] public partial string InputText { get; set; } = "Sample input text";
    public string InputLabel { get; set; } = "Sample Input Label";
    public string InputWatermark { get; set; } = "Enter text here...";
    public string BottomText { get; } = "Sample bottom text";
    public ReactiveCommand ClearInputCommand { get; set; } = new ReactiveCommand(
        executeAsync: (_, cancellationToken) => ValueTask.CompletedTask
    );
}
