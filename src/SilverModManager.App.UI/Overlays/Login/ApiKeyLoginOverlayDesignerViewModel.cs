using R3;

namespace SilverModManager.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayDesignerViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> Title { get; } = new(value: "Sign in with an API key");
    public BindableReactiveProperty<string> Instructions { get; } = new(value: "Create a personal API key on your account page, then paste it here.");
    public BindableReactiveProperty<string> Watermark { get; } = new(value: "Personal API key");
    public BindableReactiveProperty<string> PageUrl { get; } = new(value: "https://www.nexusmods.com/users/myaccount?tab=api");
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public BindableReactiveProperty<string> ErrorMessage { get; } = new(value: "Nexus Mods did not accept this key.");
    public ReactiveCommand<Unit> CommandSave { get; } = new();
    public ReactiveCommand<Unit> CommandCancel { get; } = new();
}
