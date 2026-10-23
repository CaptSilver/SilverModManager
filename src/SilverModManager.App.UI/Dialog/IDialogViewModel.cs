using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.App.UI.Dialog;

public interface IDialogViewModel : IViewModelInterface
{
    public R3.ReactiveCommand<ButtonDefinitionId, ButtonDefinitionId> ButtonPressCommand { get; }
    public string WindowTitle { get; }
    public DialogWindowSize DialogWindowSize { get; }
    public IViewModelInterface? ContentViewModel { get; }
    public DialogButtonDefinition[] ButtonDefinitions { get; }
    public bool ShowChrome { get; set; }
    public StandardDialogResult Result { get; set; }
}
