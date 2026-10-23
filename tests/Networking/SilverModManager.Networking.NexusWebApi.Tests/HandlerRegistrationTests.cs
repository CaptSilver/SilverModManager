using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using NSubstitute;
using R3;

namespace SilverModManager.Networking.NexusWebApi.Tests;

public class HandlerRegistrationTests
{
    private static (HandlerRegistration Sut, IOSInterop OsInterop, Subject<NxmHandlerSettings> Changes, Task Registered) Create(bool enabled)
    {
        var osInterop = Substitute.For<IOSInterop>();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        osInterop.RegisterUriSchemeHandler(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registered.TrySetResult();
                return ValueTask.CompletedTask;
            });
        var changes = new Subject<NxmHandlerSettings>();
        var settingsManager = Substitute.For<ISettingsManager>();
        settingsManager.Get<NxmHandlerSettings>(Arg.Any<string?>()).Returns(new NxmHandlerSettings { RegisterAsDefaultHandler = enabled });
        settingsManager.GetChanges<NxmHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(changes);

        return (new HandlerRegistration(NullLogger<HandlerRegistration>.Instance, osInterop, settingsManager), osInterop, changes, registered.Task);
    }

    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Disabled_NeverRegistersAtStartup()
    {
        var (sut, osInterop, _, _) = Create(enabled: false);

        // There is no call to wait for here, so give the startup path a moment before checking.
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        await osInterop.DidNotReceiveWithAnyArgs().RegisterUriSchemeHandler(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Enabled_RegistersNxmOnceAtStartup()
    {
        var (sut, osInterop, _, registered) = Create(enabled: true);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await registered.WaitAsync(RegistrationTimeout, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        await osInterop.Received(1).RegisterUriSchemeHandler("nxm", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TurningTheSettingOn_RegistersImmediately()
    {
        var (sut, osInterop, changes, registered) = Create(enabled: false);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await osInterop.DidNotReceiveWithAnyArgs().RegisterUriSchemeHandler(default!, default, TestContext.Current.CancellationToken);

        changes.OnNext(new NxmHandlerSettings { RegisterAsDefaultHandler = true });
        await registered.WaitAsync(RegistrationTimeout, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        await osInterop.Received(1).RegisterUriSchemeHandler("nxm", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
