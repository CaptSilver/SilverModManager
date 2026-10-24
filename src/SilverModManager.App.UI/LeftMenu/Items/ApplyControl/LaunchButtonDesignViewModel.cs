using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.LeftMenu.Items;

public partial class LaunchButtonDesignViewModel : AViewModel<ILaunchButtonViewModel>, ILaunchButtonViewModel
{
    [Reactive]
    public partial LoadoutId LoadoutId { get; set; } = Initializers.LoadoutId;

    [Reactive]
    public partial ReactiveCommand<Unit, Unit> Command { get; set; }
    
    private BehaviorSubject<bool> _isRunningSubject = new BehaviorSubject<bool>(false);
    public IObservable<bool> IsRunningObservable => _isRunningSubject.AsObservable();

    [Reactive]
    public partial string Label { get; set; } = "PLAY";

    [Reactive]
    public partial Percent? Progress { get; set; }

    [Reactive]
    public partial bool IsWaitingForSteam { get; set; }

    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public LaunchButtonDesignViewModel()
    {
        CancelCommand = ReactiveCommand.Create(() => { IsWaitingForSteam = false; });
        Command = ReactiveCommand.CreateFromTask(async () =>
        {
            Label = "PREPARING...";
            Progress = Percent.Zero;
            await Task.Delay(100);
            for (var x = 0; x < 10; x++)
            {
                Progress = Percent.CreateClamped(0.1d + Progress!.Value.Value);
                await Task.Delay(200);
            }
            
            _isRunningSubject.OnNext(true);

            Label = "GAME RUNNING...";
            Progress = null;
            await Task.Delay(2000);

            _isRunningSubject.OnNext(false);
            
            Label = "PLAY";
        });
    }
}
