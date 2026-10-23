using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public interface ICreateNewLoadoutCardViewModel : IViewModelInterface
{
    ReactiveCommand<Unit, Unit> AddLoadoutCommand { get; }
}
