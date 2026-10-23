using System.Reactive;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public interface IApplyDiffViewModel : IPageViewModelInterface
{
    public void Initialize(LoadoutId loadoutId);
    
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    
    public IViewModelInterface BodyViewModel { get; set; }
}
