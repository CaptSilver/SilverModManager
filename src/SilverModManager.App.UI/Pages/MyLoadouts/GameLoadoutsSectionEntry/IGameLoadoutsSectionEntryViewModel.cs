using System.Collections.ObjectModel;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.MyLoadouts.GameLoadoutsSectionEntry;

public interface IGameLoadoutsSectionEntryViewModel : IViewModelInterface, IDisposable
{
    string HeadingText { get; }
    ReadOnlyObservableCollection<IViewModelInterface> CardViewModels { get; }
}
