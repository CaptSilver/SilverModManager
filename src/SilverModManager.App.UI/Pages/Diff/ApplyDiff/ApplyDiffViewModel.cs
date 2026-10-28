using System.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls.Trees;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Diff.ApplyDiff;

public partial class ApplyDiffViewModel : APageViewModel<IApplyDiffViewModel>, IApplyDiffViewModel
{
    private IServiceProvider _serviceProvider;
    private readonly ILogger<ApplyDiffViewModel> _logger;
    private DiffTreeViewModel? _fileTreeViewModel;
    private LoadoutId _loadoutId;

    [Reactive] public partial IViewModelInterface BodyViewModel { get; set; } = null!;

    [Reactive] public partial bool LoadFailed { get; private set; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }


    public ApplyDiffViewModel(IWindowManager windowManager, IServiceProvider serviceProvider) : base(windowManager)
    {
        TabTitle = Language.ApplyDiffViewModel_PageTitle;
        TabIcon = IconValues.ListFilled;

        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<ApplyDiffViewModel>>();

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

        // Nothing awaits the first refresh, so a failure (an invalid loadout, a sync error) has to be logged here.
        RefreshAsync(_fileTreeViewModel).FireAndForget(_logger);
    }

    // The refresh walks the install and the loadout; the tree's own bindings marshal to the UI thread, and the
    // continuation resumes on the caller's context, so BodyViewModel is assigned there.
    private async Task RefreshAsync(DiffTreeViewModel diffTreeViewModel)
    {
        try
        {
            await Task.Run(diffTreeViewModel.Refresh);
        }
        catch
        {
            // Without this the page stays blank when the first tree cannot be built; the caller still logs the error.
            LoadFailed = true;
            throw;
        }

        LoadFailed = false;
        BodyViewModel = diffTreeViewModel;
    }
}
