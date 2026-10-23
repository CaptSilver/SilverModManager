using R3;

namespace SilverModManager.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayDesignerViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public BindableReactiveProperty<string> ErrorMessage { get; } = new(value: "Nexus Mods did not accept this key.");
    public ReactiveCommand<Unit> CommandSave { get; } = new();
    public ReactiveCommand<Unit> CommandCancel { get; } = new();
}
