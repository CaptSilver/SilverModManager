using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Networking.Steam.CLI;
using SilverModManager.Sdk.ProxyConsole;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

/// <summary>Which handler shows the sign-in QR code: the one the login's caller brought, else the registered one.</summary>
public class AuthHandlerRoutingTests
{
    private static readonly Uri Code = new("https://s.team/q/1/abc");

    private readonly IAuthInterventionHandler _registered = Substitute.For<IAuthInterventionHandler>();
    private readonly IAuthInterventionHandler _verbs = Substitute.For<IAuthInterventionHandler>();
    private readonly Session _session;

    public AuthHandlerRoutingTests()
    {
        _session = new Session(NullLogger<Session>.Instance, _registered, Substitute.For<IAuthStorage>());
    }

    [Fact]
    public void TheLoginVerbRendersTheQrThroughItsOwnRenderer()
    {
        // The verb used to depend on a static renderer that only `steam app index` set, so `steam login` threw "Renderer is not set."
        var renderer = Substitute.For<IRenderer>();
        var session = Substitute.For<ISteamSession>();
        IAuthInterventionHandler? given = null;
        session.Connect(Arg.Any<IAuthInterventionHandler>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            given = call.Arg<IAuthInterventionHandler>();
            given.ShowQRCode(Code, CancellationToken.None);
            return Task.CompletedTask;
        });

        var exit = Verbs.Login(renderer, session, CancellationToken.None).GetAwaiter().GetResult();

        exit.Should().Be(0);
        given.Should().BeOfType<RenderingAuthenticationHandler>();
        ((RenderingAuthenticationHandler)given!).Rendering.GetAwaiter().GetResult();
        renderer.Received().RenderAsync(Arg.Any<IRenderable>());
    }

    [Fact]
    public void AConnectWithoutAHandlerUsesTheRegisteredOne()
    {
        _session.ActiveHandler.Should().BeSameAs(_registered);
    }

    [Fact]
    public void ALoginStartedWithAHandlerUsesItUntilTheLoginEnds()
    {
        using (_session.BeginLogin(_verbs))
            _session.ActiveHandler.Should().BeSameAs(_verbs);

        _session.ActiveHandler.Should().BeSameAs(_registered);
    }

    [Fact]
    public void ASecondCallerJoiningALoginDoesNotMoveItsCode()
    {
        using var first = _session.BeginLogin(_verbs);
        var other = Substitute.For<IAuthInterventionHandler>();

        using (_session.BeginLogin(other))
            _session.ActiveHandler.Should().BeSameAs(_verbs);

        _session.ActiveHandler.Should().BeSameAs(_verbs, "the joiner's scope ending must not end the login's");
    }

    [Fact]
    public async Task ARefreshedChallengeGoesToTheLoginsHandlerNotTheDefault()
    {
        var challenge = "https://s.team/q/1/first";
        IAuthInterventionHandler captured;
        using (_session.BeginLogin(_verbs))
            captured = _session.ActiveHandler;
        using var presenter = new QrPresenter(captured, () => challenge);
        presenter.Show();

        // SteamKit raises the refresh from a thread of its own, after the Connect call that began the login has moved on.
        challenge = "https://s.team/q/1/second";
        await Task.Run(presenter.Show);

        _verbs.Received(1).ShowQRCode(new Uri("https://s.team/q/1/first"), Arg.Any<CancellationToken>());
        _verbs.Received(1).ShowQRCode(new Uri("https://s.team/q/1/second"), Arg.Any<CancellationToken>());
        _registered.DidNotReceiveWithAnyArgs().ShowQRCode(default!, default);
    }

    [Fact]
    public void TheHandlerIsToldToHideTheCodeWhenThePresenterEnds()
    {
        CancellationToken shown = default;
        _verbs.When(h => h.ShowQRCode(Arg.Any<Uri>(), Arg.Any<CancellationToken>())).Do(call => shown = call.Arg<CancellationToken>());
        var presenter = new QrPresenter(_verbs, () => Code.ToString());
        presenter.Show();
        shown.IsCancellationRequested.Should().BeFalse();

        presenter.Dispose();

        shown.IsCancellationRequested.Should().BeTrue();
    }
}
