using System.Reactive;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public class ApplyDiffDesignViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
{
    public ApplyDiffDesignViewModel(IWindowManager windowManager) : base(windowManager)
    {
    }
    
    public ApplyDiffDesignViewModel() : base(new DesignWindowManager())
    {
    }

    public void Initialize(LoadoutId loadoutId)
    {
        throw new NotImplementedException();
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; } = ReactiveCommand.Create(() => { });
    public IViewModelInterface BodyViewModel { get; set; } = null!;
}
