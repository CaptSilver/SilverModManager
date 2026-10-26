using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Ini;

[UsedImplicitly]
public partial class IniPageViewModel : APageViewModel<IIniPageViewModel>, IIniPageViewModel
{
    private readonly IServiceProvider _services;
    private readonly IConnection _connection;
    private readonly IErrorPresenter _errors;

    [Reactive] public partial LoadoutId LoadoutId { get; set; }
    [Reactive] public partial IReadOnlyList<IniFileEditor> Files { get; private set; } = [];
    [Reactive] public partial IniFileEditor? SelectedFile { get; set; }

    public IniPageViewModel(IServiceProvider services, IWindowManager windowManager) : base(windowManager)
    {
        _services = services;
        _connection = services.GetRequiredService<IConnection>();
        _errors = services.GetRequiredService<IErrorPresenter>();

        TabIcon = IconValues.FileEdit;
        TabTitle = Language.IniPage_Title;

        this.WhenAnyValue(vm => vm.LoadoutId).Subscribe(id =>
        {
            if (id != default) _ = Open(id);
        });
    }

    private async Task Open(LoadoutId id)
    {
        try
        {
            var loadout = Loadout.Load(_connection.Db, id);
            if (loadout.InstallationInstance.Game is not IGameIniFiles game)
            {
                Files = [];
                SelectedFile = null;
                return;
            }

            var files = IniFileEditor.ForGame(loadout, game, _services);
            foreach (var file in files) await file.Load();
            Files = files;
            SelectedFile = files.FirstOrDefault();
        }
        catch (Exception exception)
        {
            await _errors.Report(exception, "opening the INI page");
        }
    }
}
