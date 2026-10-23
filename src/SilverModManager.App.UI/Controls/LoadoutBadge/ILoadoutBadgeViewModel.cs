using DynamicData.Kernel;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.LoadoutBadge;

public interface ILoadoutBadgeViewModel : IViewModelInterface
{
    Optional<Loadout.ReadOnly> LoadoutValue { get; set;  }
    
    string LoadoutShortName { get; }
    
    bool IsLoadoutSelected { get; set; }
    
    bool IsLoadoutApplied { get; }
    
    bool IsLoadoutInProgress { get; }
    
    bool IsVisible { get; }
}
