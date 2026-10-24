using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Aggregation;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages.SteamLaunchOption;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

public partial class LoadoutCardViewModel : AViewModel<ILoadoutCardViewModel>, ILoadoutCardViewModel
{
    private readonly ILogger<LoadoutCardViewModel> _logger;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IServiceProvider _serviceProvider;

    public LoadoutCardViewModel(Loadout.ReadOnly loadout, IConnection conn, IServiceProvider serviceProvider)
    {
        LoadoutVal = loadout;
        _logger = serviceProvider.GetRequiredService<ILogger<LoadoutCardViewModel>>();
        _serviceProvider = serviceProvider;
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        var applyService = serviceProvider.GetRequiredService<ISynchronizerService>();
        LoadoutName = loadout.Name;
        var badgeVm = serviceProvider.GetRequiredService<ILoadoutBadgeViewModel>();
        badgeVm.LoadoutValue = loadout;
        LoadoutBadgeViewModel = badgeVm;
        
        DeleteLoadoutCommand = ReactiveCommand.CreateFromTask(() =>
            {
                IsDeleting = true;
                return DeleteLoadout(loadout);
            }
        );
        
        CloneLoadoutCommand = ReactiveCommand.CreateFromTask(() =>
            {
                return CopyLoadout(loadout);
            }
        );
        
        RenameLoadoutCommand = ReactiveCommand.CreateFromTask(() => RenameLoadout(loadout));

        UseFromSteamCommand = ReactiveCommand.CreateFromTask(() => ShowSteamLaunchOption(loadout));
        CanUseFromSteam = loadout.Installation.Store == GameStore.Steam;

        this.WhenActivated(d =>
        {
            Observable.FromAsync(() => LoadImage(loadout.InstallationInstance))
                .OnUI()
                .BindToVM(this, x => x.LoadoutImage)
                .DisposeWith(d);
            
            Loadout.Observe(conn, loadout.Id)
                .Select(l => l.Name)
                .OnUI()
                .BindToVM(this, x => x.LoadoutName)
                .DisposeWith(d);
            
            applyService.LastAppliedRevisionFor(loadout.InstallationInstance)
                .Select(rev => rev.Id == loadout.LoadoutId)
                .OnUI()
                .BindToVM(this, x => x.IsLoadoutApplied)
                .DisposeWith(d);

            var interval = Observable.Interval(TimeSpan.FromMinutes(1)).Prepend(0);
            
            interval.Select(_ => FormatCreatedTime(loadout.GetCreatedAt()))
                .OnUI()
                .BindToVM(this, x => x.HumanizedLoadoutCreationTime)
                .DisposeWith(d);

            interval.Select(_ =>
                    {
                        if (loadout.LastAppliedDateTime.TryGet(out var lastAppliedTime))
                        {
                            return FormatLastAppliedTime(lastAppliedTime);
                        }
                        return "";
                    }
                )
                .OnUI()
                .BindToVM(this, x => x.HumanizedLoadoutLastApplyTime)
                .DisposeWith(d);

            LoadoutDataProviderHelper
                .CountAllLoadoutItems(serviceProvider, loadout.LoadoutId)
                .Select(FormatNumMods)
                .OnUI()
                .BindToVM(this, x => x.LoadoutModCount)
                .DisposeWith(d);

            Loadout.ObserveAll(conn)
                .Filter(l => l.IsVisible() && l.Installation.Path == loadout.Installation.Path && l.LoadoutId != loadout.LoadoutId)
                .Count()
                .Select(count => count == 0)
                .OnUI()
                .BindToVM(this, x => x.IsLastLoadout)
                .DisposeWith(d);

        });
        
    }
    
    public Loadout.ReadOnly LoadoutVal { get; }
    public ILoadoutBadgeViewModel LoadoutBadgeViewModel { get; private set; }
    [Reactive] public partial string LoadoutName { get; private set; }
    [Reactive] public partial IImage? LoadoutImage { get; private set; } 
    [Reactive] public partial bool IsLoadoutApplied { get; private set; } = false;
    [Reactive] public partial string HumanizedLoadoutLastApplyTime { get; private set; } = "";
    [Reactive] public partial string HumanizedLoadoutCreationTime { get; private set; } = "";
    [Reactive] public partial string LoadoutModCount { get; private set; } = "";
    [Reactive] public partial bool IsDeleting { get;  private set; } = false;
    public bool IsSkeleton => false;
    public required ReactiveCommand<Unit, Unit> VisitLoadoutCommand { get; init; }
    public ReactiveCommand<Unit, Unit> CloneLoadoutCommand { get; } 
    public ReactiveCommand<Unit, Unit> DeleteLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> RenameLoadoutCommand { get; }
    public ReactiveCommand<Unit, Unit> UseFromSteamCommand { get; }
    public bool CanUseFromSteam { get; }
    
    [Reactive] public partial bool IsLastLoadout { get; set; } = true;
    
    
    private static string FormatNumMods(int numMods)
    {
        return string.Format(Language.LoadoutCardViewModel_FormatNumMods_Mods__0_, numMods);
    }
    
    private static string FormatCreatedTime(DateTimeOffset creationTime)
    {
        return string.Format(Language.LoadoutCardViewModel_CreationTimeConverter_Created__0_, creationTime.Humanize());
    }
    
    private static string FormatLastAppliedTime(DateTimeOffset lastAppliedTime)
    {
        var stringTime = lastAppliedTime == DateTimeOffset.MinValue ? Language.HumanizedDateTime_Never : lastAppliedTime.Humanize();
        return string.Format(Language.LoadoutCardViewModel_FormatLastAppliedTime_Last_applied__0_, stringTime);
    }

    private async Task<Bitmap?> LoadImage(GameInstallation source)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var stream = await source.GetGame().IconImage.GetStreamAsync();
                return Bitmap.DecodeToWidth(stream, (int) ImageSizes.GameThumbnail.Width);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "While loading game image for {GameName}", source.Game.DisplayName);
                return null;
            }
        });
    }
    
    private async Task DeleteLoadout(Loadout.ReadOnly loadout)
    {
        var deleted = await MessageBoxOkViewModel.RunOrExplainRunningGame(
            _serviceProvider,
            loadout.Installation.Name,
            () => Task.Run(() => _loadoutManager.DeleteLoadout(loadout).AsTask())
        );

        // The card stays in the list when the delete was refused.
        if (!deleted) IsDeleting = false;
    }

    private Task CopyLoadout(Loadout.ReadOnly loadout)
    {
        return Task.Run(() => _loadoutManager.CopyLoadout(loadout));
    }

    private async Task RenameLoadout(Loadout.ReadOnly loadout)
    {
        var windowManager = _serviceProvider.GetRequiredService<IWindowManager>();
        var result = await windowManager.ShowDialog(LoadoutDialogs.RenameLoadout(LoadoutName), DialogWindowType.Modal);
        if (result.ButtonId != ButtonDefinitionId.Accept || string.IsNullOrWhiteSpace(result.InputText)) return;

        // Uniqueness within the installation is the manager's call, so the stored name can differ from the input.
        await Task.Run(() => _loadoutManager.RenameLoadout(loadout.LoadoutId, result.InputText).AsTask());
    }

    private Task ShowSteamLaunchOption(Loadout.ReadOnly loadout) => SteamLaunchOptionDialog.ShowAsync(_serviceProvider, loadout);
}
