using R3;

namespace NexusMods.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayDesignerViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public ReactiveCommand<Unit> CommandSave { get; } = new();
    public ReactiveCommand<Unit> CommandCancel { get; } = new();
}
