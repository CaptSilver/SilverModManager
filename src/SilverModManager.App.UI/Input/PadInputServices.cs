using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Input.Bindings;
using SilverModManager.App.UI.Input.Reorder;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Settings;
using SilverModManager.Platform.Gamepad;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.App.UI.Input;

public static class PadInputServices
{
    /// <summary>
    /// The pad source, the input mode, the navigator and the bindings it tries in order: the first that matches and can
    /// run wins, so the narrower bindings (a spine's shoulders, a tab control's) come before the general ones.
    /// </summary>
    public static IServiceCollection AddPadInput(this IServiceCollection services)
    {
        return services
            .AddSingleton<IGamepadSource>(provider =>
            {
                var enabled = provider.GetRequiredService<ISettingsManager>().Get<GameModeSettings>().Controller;
                var source = GamepadSourceFactory.Create(provider.GetRequiredService<ILoggerFactory>().CreateLogger("SilverModManager.Platform.Gamepad"), enabled);
                GameModeSettings.ReportGamepadSource(source);
                return source;
            })
            .AddSingleton<IInputModeService, InputModeService>()
            .AddSingleton<ReorderState>()
            .AddSingleton(_ => new KeyOwnershipFilter(new GamepadPresence(TimeProvider.System), new GamepadPresence(TimeProvider.System)))
            // Before everything else: while a row is lifted these take its buttons, and the guard takes the rest.
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.Drop, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.Cancel, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.Up, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.Down, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.PageUp, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.PageDown, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderBinding(ReorderStep.PickUp, provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding>(provider => new ReorderGuardBinding(provider.GetRequiredService<ReorderState>()))
            .AddSingleton<IPadBinding, MenuBinding>(_ => new MenuBinding())
            .AddSingleton<IPadBinding>(_ => new SpineCycleBinding(forward: true))
            .AddSingleton<IPadBinding>(_ => new SpineCycleBinding(forward: false))
            .AddSingleton<IPadBinding>(_ => new TabCycleBinding(forward: true))
            .AddSingleton<IPadBinding>(_ => new TabCycleBinding(forward: false))
            .AddSingleton<IPadBinding, ContextMenuBinding>()
            .AddSingleton<IPadBinding, ScrollBinding>()
            .AddSingleton<IPadBinding, FocusMoveBinding>()
            .AddSingleton<IPadBinding, ActivateBinding>()
            .AddSingleton<IPadBinding, BackBinding>()
            .AddSingleton(provider =>
            {
                // The window check needs the navigator's window, and the navigator needs the check: this holds the way round.
                var created = new StrongBox<GamepadNavigator?>();
                var navigator = new GamepadNavigator(
                    provider.GetRequiredService<IGamepadSource>(),
                    provider.GetRequiredService<IInputModeService>(),
                    provider.GetRequiredService<KeyOwnershipFilter>(),
                    provider.GetServices<IPadBinding>(),
                    () => created.Value?.Root is Window { IsActive: true },
                    provider.GetRequiredService<IOverlayController>());
                created.Value = navigator;
                return navigator;
            })
            .AddHostedService<GamepadNavigatorHost>();
    }
}
