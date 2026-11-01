using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.CLI.Types;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Which schemes the app claims and when, over a substituted OS layer, settings store and event bus. Nothing here
/// reaches xdg-mime, xdg-settings or the user's desktop files.
/// </summary>
public class ProtocolRegistrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Rig
    {
        public IOSInterop Os { get; } = Substitute.For<IOSInterop>();
        public IEventBus Events { get; } = Substitute.For<IEventBus>();
        public ISettingsManager Settings { get; } = Substitute.For<ISettingsManager>();
        public Subject<NxmHandlerSettings> NxmChanges { get; } = new();
        public Subject<Ror2mmHandlerSettings> Ror2mmChanges { get; } = new();
        public ProtocolNoticeSettings Notices { get; private set; } = new();
        public List<(string Scheme, UriSchemeClaim Claim)> Calls { get; } = [];

        private readonly SemaphoreSlim _registered = new(0);

        public Rig(bool nxm, bool ror2mm, UriSchemeRegistration? answer = null)
        {
            Settings.Get<NxmHandlerSettings>(Arg.Any<string?>()).Returns(new NxmHandlerSettings { RegisterAsDefaultHandler = nxm });
            Settings.Get<Ror2mmHandlerSettings>(Arg.Any<string?>()).Returns(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = ror2mm });
            Settings.GetChanges<NxmHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(NxmChanges);
            Settings.GetChanges<Ror2mmHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(Ror2mmChanges);
            Settings.Get<ProtocolNoticeSettings>(Arg.Any<string?>()).Returns(_ => Notices);
            Settings.Update(Arg.Any<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>(), Arg.Any<string?>())
                .Returns(call => Notices = call.Arg<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>()(Notices));

            Os.RegisterUriSchemeHandler(Arg.Any<string>(), Arg.Any<UriSchemeClaim>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add((call.ArgAt<string>(0), call.ArgAt<UriSchemeClaim>(1)));
                    _registered.Release();
                    return new ValueTask<UriSchemeRegistration>(answer ?? new UriSchemeRegistration(true, null));
                });
        }

        public void Answer(UriSchemeRegistration answer)
            => Os.RegisterUriSchemeHandler(Arg.Any<string>(), Arg.Any<UriSchemeClaim>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Calls.Add((call.ArgAt<string>(0), call.ArgAt<UriSchemeClaim>(1)));
                    _registered.Release();
                    return new ValueTask<UriSchemeRegistration>(answer);
                });

        public ProtocolRegistration Create()
        {
            var services = new ServiceCollection().AddSingleton(Events).BuildServiceProvider();
            IIpcProtocolHandler[] handlers =
            [
                new NxmIpcProtocolHandler(services, NullLogger<NxmIpcProtocolHandler>.Instance, null!, null!, null!, null!),
                new Ror2mmIpcProtocolHandler(
                    NullLogger<Ror2mmIpcProtocolHandler>.Instance,
                    Events,
                    Substitute.For<IThunderstoreInstaller>(),
                    Substitute.For<ManagedGames>(Substitute.For<IGameRegistry>(), Substitute.For<IConnection>(), Substitute.For<ISynchronizerService>())),
            ];
            return new ProtocolRegistration(handlers, Os, Settings, Events, NullLogger<ProtocolRegistration>.Instance);
        }

        public Task WaitForRegistrations(int count) => Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                await _registered.WaitAsync(Timeout);
        });
    }

    private static async Task<ProtocolRegistration> Start(Rig rig, int expectedCalls)
    {
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await rig.WaitForRegistrations(expectedCalls);
        return sut;
    }

    // There is no call to wait for in the "nothing happens" cases, so give the startup path a moment first.
    private static async Task Settle() => await Task.Delay(150, TestContext.Current.CancellationToken);

    [Fact]
    public async Task NxmWanted_RegistersNxmOnceAtStartupWithoutStealingIt()
    {
        var rig = new Rig(nxm: true, ror2mm: false);

        var sut = await Start(rig, 1);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Equal(("nxm", UriSchemeClaim.IfUnclaimed));
    }

    [Fact]
    public async Task Ror2mmWanted_RegistersRor2mm()
    {
        var rig = new Rig(nxm: false, ror2mm: true);

        var sut = await Start(rig, 1);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Equal(("ror2mm", UriSchemeClaim.IfUnclaimed));
    }

    [Fact]
    public async Task BothWanted_RegistersEachSchemeOnce()
    {
        var rig = new Rig(nxm: true, ror2mm: true);

        var sut = await Start(rig, 2);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().BeEquivalentTo([("nxm", UriSchemeClaim.IfUnclaimed), ("ror2mm", UriSchemeClaim.IfUnclaimed)]);
    }

    [Fact]
    public async Task NeitherWanted_RegistersNothing()
    {
        var rig = new Rig(nxm: false, ror2mm: false);

        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TurningNxmOnLater_TakesTheSchemeRightAway()
    {
        var rig = new Rig(nxm: false, ror2mm: false);
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Settle();
        rig.Calls.Should().BeEmpty();

        rig.NxmChanges.OnNext(new NxmHandlerSettings { RegisterAsDefaultHandler = true });
        await rig.WaitForRegistrations(1);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Equal(("nxm", UriSchemeClaim.Take));
    }

    [Fact]
    public async Task TurningRor2mmOnLater_TakesTheSchemeRightAway()
    {
        var rig = new Rig(nxm: false, ror2mm: false);
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);

        rig.Ror2mmChanges.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = true });
        await rig.WaitForRegistrations(1);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Equal(("ror2mm", UriSchemeClaim.Take));
    }

    [Fact]
    public async Task ASettingChangeThatStaysOff_RegistersNothing()
    {
        var rig = new Rig(nxm: false, ror2mm: false);
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);

        rig.Ror2mmChanges.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = false });
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task TakingASchemeAnotherAppHeld_ReportsTheFormerHolder()
    {
        var rig = new Rig(nxm: false, ror2mm: false);
        rig.Answer(new UriSchemeRegistration(true, "r2modman.desktop"));
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);

        rig.Ror2mmChanges.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = true });
        await rig.WaitForRegistrations(1);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Events.Received(1).Send(new CliMessages.ProtocolHandlerTaken("ror2mm", "r2modman.desktop"));
        rig.Events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerHeldElsewhere>());
    }

    [Fact]
    public async Task TakingAnUnheldScheme_ReportsNothing()
    {
        var rig = new Rig(nxm: false, ror2mm: false);
        var sut = rig.Create();
        await sut.StartAsync(TestContext.Current.CancellationToken);

        rig.NxmChanges.OnNext(new NxmHandlerSettings { RegisterAsDefaultHandler = true });
        await rig.WaitForRegistrations(1);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerTaken>());
    }

    [Fact]
    public async Task StartupFindingAnotherHolder_ReportsItAndRegistersOnlyOnce()
    {
        var rig = new Rig(nxm: false, ror2mm: true, new UriSchemeRegistration(false, "r2modman.desktop"));

        var sut = await Start(rig, 1);
        await Settle();
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Equal(("ror2mm", UriSchemeClaim.IfUnclaimed));
        rig.Events.Received(1).Send(new CliMessages.ProtocolHandlerHeldElsewhere("ror2mm", "r2modman.desktop"));
        rig.Events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerTaken>());
    }

    [Fact]
    public async Task ASecondStartWithTheSameHolder_PublishesNothing()
    {
        var rig = new Rig(nxm: false, ror2mm: true, new UriSchemeRegistration(false, "r2modman.desktop"));
        var first = await Start(rig, 1);
        await first.StopAsync(TestContext.Current.CancellationToken);
        rig.Events.ClearReceivedCalls();

        var second = await Start(rig, 1);
        await Settle();
        await second.StopAsync(TestContext.Current.CancellationToken);

        rig.Events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerHeldElsewhere>());
    }

    [Fact]
    public async Task ASecondStartWithADifferentHolder_PublishesAgain()
    {
        var rig = new Rig(nxm: false, ror2mm: true, new UriSchemeRegistration(false, "r2modman.desktop"));
        var first = await Start(rig, 1);
        await first.StopAsync(TestContext.Current.CancellationToken);
        rig.Answer(new UriSchemeRegistration(false, "gale.desktop"));

        var second = await Start(rig, 1);
        await Settle();
        await second.StopAsync(TestContext.Current.CancellationToken);

        rig.Events.Received(1).Send(new CliMessages.ProtocolHandlerHeldElsewhere("ror2mm", "gale.desktop"));
    }

    [Fact]
    public async Task ASuccessfulTake_ForgetsTheHolderSoItIsReportedAgainLater()
    {
        var rig = new Rig(nxm: false, ror2mm: true, new UriSchemeRegistration(false, "r2modman.desktop"));
        var first = await Start(rig, 1);
        rig.Answer(new UriSchemeRegistration(true, null));
        rig.Ror2mmChanges.OnNext(new Ror2mmHandlerSettings { RegisterAsDefaultHandler = true });
        await rig.WaitForRegistrations(1);
        await first.StopAsync(TestContext.Current.CancellationToken);
        rig.Notices.LastReportedHolder.Should().NotContainKey("ror2mm");

        rig.Answer(new UriSchemeRegistration(false, "r2modman.desktop"));
        rig.Events.ClearReceivedCalls();
        var second = await Start(rig, 1);
        await second.StopAsync(TestContext.Current.CancellationToken);

        rig.Events.Received(1).Send(new CliMessages.ProtocolHandlerHeldElsewhere("ror2mm", "r2modman.desktop"));
    }

    [Fact]
    public async Task ARegistrationThatThrows_DoesNotStopTheNextOne()
    {
        var rig = new Rig(nxm: true, ror2mm: true);
        rig.Os.RegisterUriSchemeHandler("nxm", Arg.Any<UriSchemeClaim>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("desktop file unwritable"));

        var sut = await Start(rig, 1);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        rig.Calls.Should().Contain(("ror2mm", UriSchemeClaim.IfUnclaimed));
    }
}
