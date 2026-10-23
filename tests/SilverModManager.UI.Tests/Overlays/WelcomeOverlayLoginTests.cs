using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Login;
using SilverModManager.Sdk;
using SilverModManager.UI.Sdk;
using NSubstitute;
using R3;

namespace SilverModManager.UI.Tests.Overlays;

public class WelcomeOverlayLoginTests
{
    [Fact]
    public async Task LoggingInFromTheWelcomeOverlayShowsTheKeyDialogInsteadOfQueueingItBehind()
    {
        var controller = new OverlayController();
        var loginManager = Substitute.For<ILoginManager>();
        loginManager.IsLoggedInObservable.Returns(System.Reactive.Linq.Observable.Return(false));

        var prompt = new ApiKeyPrompt(controller);
        loginManager.LoginAsync(Arg.Any<CancellationToken>()).Returns(_ => prompt.PromptAsync((_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None).AsTask());

        var welcome = new WelcomeOverlayViewModel(Substitute.For<IOSInterop>(), loginManager, Substitute.For<IWindowNotificationService>());
        controller.Enqueue(welcome);

        welcome.CommandLogIn.Execute(Unit.Default);

        controller.CurrentOverlay.Should().BeOfType<ApiKeyLoginOverlayViewModel>("the dialog the user asked for must not sit under the welcome overlay");
        welcome.Status.Should().Be(Status.Closed);

        ((ApiKeyLoginOverlayViewModel)controller.CurrentOverlay!).CommandCancel.Execute(Unit.Default);
        await Task.Yield();
    }
}
