using System.ComponentModel;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using ReactiveUI;

namespace SilverModManager.App.UI.Dialog;

public class DialogViewModel: IDialogViewModel
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ViewModelActivator Activator { get; }
    public R3.ReactiveCommand<ButtonDefinitionId, ButtonDefinitionId> ButtonPressCommand { get; }
    public string WindowTitle { get; }
    public DialogWindowSize DialogWindowSize { get; }
    public IViewModelInterface? ContentViewModel { get; }
    public DialogButtonDefinition[] ButtonDefinitions { get; }
    public bool ShowChrome { get; set; }
    public StandardDialogResult Result { get; set; }

    public DialogViewModel(string title, DialogButtonDefinition[] buttonsDefinitions, IViewModelInterface contentViewModel, DialogWindowSize dialogWindowSize, bool showChrome) {
        Activator = new ViewModelActivator();
        WindowTitle = title;
        DialogWindowSize = dialogWindowSize;
        ContentViewModel = contentViewModel;
        ButtonDefinitions = buttonsDefinitions;
        ShowChrome = showChrome;
        Result = new StandardDialogResult();
        
        ButtonPressCommand = new R3.ReactiveCommand<ButtonDefinitionId, ButtonDefinitionId>(id =>
            {
                Console.WriteLine(id);
                Result = new StandardDialogResult
                {
                    ButtonId = id,
                    InputText = contentViewModel is IDialogStandardContentViewModel standardContentViewModel
                        ? standardContentViewModel.InputText
                        : string.Empty
                };
                return id;
            }
        );
    }
    
}
