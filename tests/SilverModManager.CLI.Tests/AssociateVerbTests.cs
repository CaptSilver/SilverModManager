using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The associate-nxm and associate-ror2mm verbs, run through the command line over a substituted OS layer. Nothing
/// here reaches xdg-mime, xdg-settings or the user's desktop files.
/// </summary>
public class AssociateVerbTests
{
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();
    private readonly IEventBus _events = Substitute.For<IEventBus>();
    private readonly ISettingsManager _settings = Substitute.For<ISettingsManager>();

    private CommandLineConfigurator Configure()
    {
        _settings.Get<ProtocolNoticeSettings>(Arg.Any<string?>()).Returns(new ProtocolNoticeSettings());
        var provider = new ServiceCollection()
            .AddLogging()
            .AddOptionParser<Uri>(u => (new Uri(u), null))
            .AddSingleton(_os)
            .AddSingleton(_events)
            .AddSingleton(_settings)
            .AddSingleton<ProtocolNotices>()
            .AddProtocolVerbs()
            .BuildServiceProvider();
        return new CommandLineConfigurator(provider, provider.GetServices<VerbDefinition>(), provider.GetServices<ModuleDefinition>());
    }

    private void RegistrationReturns(string scheme, UriSchemeRegistration result) =>
        _os.RegisterUriSchemeHandler(scheme, UriSchemeClaim.Take, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UriSchemeRegistration>(result));

    [Theory]
    [InlineData("nxm")]
    [InlineData("ror2mm")]
    public async Task TheVerb_TakesItsOwnSchemeWithTheTakeClaim(string scheme)
    {
        RegistrationReturns(scheme, new UriSchemeRegistration(true, null));

        var exitCode = await Configure().RunAsync([$"associate-{scheme}"], new LoggingRenderer(), CancellationToken.None);

        exitCode.Should().Be(0);
        await _os.Received(1).RegisterUriSchemeHandler(scheme, UriSchemeClaim.Take, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _os.DidNotReceive().RegisterUriSchemeHandler(Arg.Is<string>(s => s != scheme), Arg.Any<UriSchemeClaim>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("nxm", "vortex.desktop")]
    [InlineData("ror2mm", "r2modman.desktop")]
    public async Task TakingASchemeAnotherAppHeld_PublishesProtocolHandlerTaken(string scheme, string previous)
    {
        RegistrationReturns(scheme, new UriSchemeRegistration(true, previous));

        await Configure().RunAsync([$"associate-{scheme}"], new LoggingRenderer(), CancellationToken.None);

        _events.Received(1).Send(new CliMessages.ProtocolHandlerTaken(scheme, previous));
    }

    [Fact]
    public async Task TakingASchemeNobodyHeld_PublishesNothing()
    {
        RegistrationReturns("ror2mm", new UriSchemeRegistration(true, null));

        await Configure().RunAsync(["associate-ror2mm"], new LoggingRenderer(), CancellationToken.None);

        _events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerTaken>());
    }

    [Fact]
    public async Task ATakeThatFails_PublishesNothing()
    {
        RegistrationReturns("ror2mm", new UriSchemeRegistration(false, "r2modman.desktop"));

        await Configure().RunAsync(["associate-ror2mm"], new LoggingRenderer(), CancellationToken.None);

        _events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerTaken>());
    }
}
