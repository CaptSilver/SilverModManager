using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Notifications;
using SilverModManager.Platform.Proton;
using SilverModManager.Sdk;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Notifications;

public class InAppNotifierTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public void AddUiReplacesTheHeadlessNotifier()
    {
        var services = new ServiceCollection().AddHeadlessLaunch().AddUI();

        var notifiers = services.Where(d => d.ServiceType == typeof(IDesktopNotifier)).ToArray();

        notifiers.Should().ContainSingle().Which.ImplementationType.Should().Be(typeof(InAppNotifier));
    }

    [Fact]
    public async Task NotifyShowsAnErrorToastWithSummaryAndBody()
    {
        var toasts = Substitute.For<IWindowNotificationService>();
        var notifier = new InAppNotifier(toasts, NullLogger<InAppNotifier>.Instance);

        notifier.Notify("SilverModManager", "Test Game is already running.");
        await Dispatcher.UIThread.InvokeAsync(() => { });

        toasts.Received(1).ShowToast("SilverModManager: Test Game is already running.", ToastNotificationVariant.Failure, TimeSpan.Zero);
    }

    [Fact]
    public async Task NotifyEscapesTheTextSoAGameNameCannotBecomeMarkdown()
    {
        var toasts = Substitute.For<IWindowNotificationService>();
        var notifier = new InAppNotifier(toasts, NullLogger<InAppNotifier>.Instance);

        notifier.Notify("SilverModManager", "[click](https://example.com)");
        await Dispatcher.UIThread.InvokeAsync(() => { });

        toasts.Received(1).ShowToast(Arg.Is<string>(m => !m.Contains("](")), ToastNotificationVariant.Failure, TimeSpan.Zero);
    }
}
