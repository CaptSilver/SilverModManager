using System.Reactive;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public partial class ApplyDiffViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
{
    private IServiceProvider _serviceProvider;
    private DiffTreeViewModel? _fileTreeViewModel;
    private LoadoutId _loadoutId;

    [Reactive] public partial IViewModelInterface BodyViewModel { get; set; } = null!;

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }


    public ApplyDiffViewModel(IWindowManager windowManager, IServiceProvider serviceProvider) : base(windowManager)
    {
        TabTitle = Language.ApplyDiffViewModel_PageTitle;
        TabIcon = IconValues.ListFilled;

        _serviceProvider = serviceProvider;

        RefreshCommand = ReactiveCommand.Create( () =>
        {
            if (_fileTreeViewModel is null)
            {
                return;
            }

            Refresh(_fileTreeViewModel);
        });
    }


    public void Initialize(LoadoutId loadoutId)
    {
        _loadoutId = loadoutId;
        _fileTreeViewModel = new DiffTreeViewModel(_loadoutId,
            _serviceProvider.GetRequiredService<ISynchronizerService>(),
            _serviceProvider.GetRequiredService<IConnection>()
        );

        Refresh(_fileTreeViewModel);
    }
    
    private void Refresh(DiffTreeViewModel diffTreeViewModel)
    {
        diffTreeViewModel.Refresh();

        Dispatcher.UIThread.Invoke(() =>
        {
            BodyViewModel = diffTreeViewModel;
        });
    }
}
