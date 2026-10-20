using R3;

namespace NexusMods.App.UI.Overlays.Login;

public class ApiKeyLoginOverlayViewModel : AOverlayViewModel<IApiKeyLoginOverlayViewModel, string>, IApiKeyLoginOverlayViewModel
{
    public BindableReactiveProperty<string> ApiKey { get; } = new(value: "");
    public ReactiveCommand<Unit> CommandSave { get; }
    public ReactiveCommand<Unit> CommandCancel { get; }

    public ApiKeyLoginOverlayViewModel()
    {
        CommandSave = ApiKey
            .Select(static key => !string.IsNullOrWhiteSpace(key))
            .ToReactiveCommand<Unit>(_ => Complete(ApiKey.Value.Trim()));

        // Close rather than Complete: a cancelled dialog has no result, and the caller reads null as "cancelled".
        CommandCancel = new ReactiveCommand<Unit>(_ => Close());
    }
}
