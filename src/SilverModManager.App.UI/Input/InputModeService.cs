using Avalonia;
using Avalonia.Threading;
using R3;
using SilverModManager.Sdk;

namespace SilverModManager.App.UI.Input;

/// <inheritdoc />
public sealed class InputModeService : IInputModeService
{
    /// <summary>The boolean resource the theme's popup style reads; the theme declares it false.</summary>
    public const string PopupsInOverlayLayerKey = "PopupsInOverlayLayer";

    private readonly IGameModeDetector _gameMode;
    private readonly BehaviorSubject<InputMode> _modes = new(InputMode.Pointer);

    public InputModeService(IGameModeDetector gameMode)
    {
        _gameMode = gameMode;
        ApplyResource();
    }

    public InputMode Mode => _modes.Value;

    public Observable<InputMode> Modes => _modes;

    public bool UsesOverlayPopups => Mode == InputMode.Pad || _gameMode.IsGameMode;

    public void PadUsed() => Set(InputMode.Pad);

    public void PointerMoved() => Set(InputMode.Pointer);

    private void Set(InputMode mode)
    {
        if (_modes.Value == mode) return;

        _modes.OnNext(mode);
        ApplyResource();
    }

    private void ApplyResource()
    {
        if (Application.Current is not { } app) return;

        var value = UsesOverlayPopups;
        // Resources belong to the UI thread, and the service can be built on another one.
        if (Dispatcher.UIThread.CheckAccess()) app.Resources[PopupsInOverlayLayerKey] = value;
        else Dispatcher.UIThread.Post(() => app.Resources[PopupsInOverlayLayerKey] = value);
    }
}
