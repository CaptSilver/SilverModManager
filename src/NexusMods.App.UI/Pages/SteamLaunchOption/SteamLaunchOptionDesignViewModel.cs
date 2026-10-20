using System.Reactive;
using NexusMods.App.UI.Overlays;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

public class SteamLaunchOptionDesignViewModel : AOverlayViewModel<ISteamLaunchOptionViewModel, Unit>, ISteamLaunchOptionViewModel
{
    public string LoadoutName => "Loadout A";
    public string Line => "'/home/user/.local/share/silvermod/bin/silvermod' run 'Loadout A' -- %command%";
    public string Warning => "";
    public string CopyNote => "Copying changes nothing in Steam. Paste the line into the game's Properties > Launch Options yourself.";
    public bool IsSteamRunning => true;
    public bool CanWrite => false;
    public string WriteNote => "Close Steam completely, including its tray icon, to write the launch option.";
    public string Message => "";
    public ReactiveCommand<Unit, Unit> CopyCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> WriteCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> CloseCommand { get; } = ReactiveCommand.Create(() => { });
}
