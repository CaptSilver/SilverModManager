using System.Reactive;
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

        RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_fileTreeViewModel is null)
            {
                return;
            }

            await RefreshAsync(_fileTreeViewModel);
        });
    }


    public void Initialize(LoadoutId loadoutId)
    {
        _loadoutId = loadoutId;
        _fileTreeViewModel = new DiffTreeViewModel(_loadoutId,
            _serviceProvider.GetRequiredService<ISynchronizerService>(),
            _serviceProvider.GetRequiredService<IConnection>()
        );

        _ = RefreshAsync(_fileTreeViewModel);
    }

    // The refresh walks the install and the loadout; the tree's own bindings marshal to the UI thread, and the
    // continuation resumes on the caller's context, so BodyViewModel is assigned there.
    private async Task RefreshAsync(DiffTreeViewModel diffTreeViewModel)
    {
        await Task.Run(diffTreeViewModel.Refresh);
        BodyViewModel = diffTreeViewModel;
    }
}
