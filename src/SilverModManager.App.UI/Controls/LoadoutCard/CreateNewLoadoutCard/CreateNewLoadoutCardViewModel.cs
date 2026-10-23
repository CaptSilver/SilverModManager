using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public class CreateNewLoadoutCardViewModel : AViewModel<ICreateNewLoadoutCardViewModel>, ICreateNewLoadoutCardViewModel
{
    public required ReactiveCommand<Unit, Unit> AddLoadoutCommand { get; init; } 
}
