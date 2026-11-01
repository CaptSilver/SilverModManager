using FluentAssertions;
using NSubstitute;
using SilverModManager.Sdk;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The held-elsewhere notice is remembered as reported only once someone acknowledges it, because the startup pass
/// runs before any window exists to show it.
/// </summary>
public class ProtocolNoticesTests
{
    private readonly IEventBus _events = Substitute.For<IEventBus>();
    private readonly ISettingsManager _settings = Substitute.For<ISettingsManager>();
    private ProtocolNoticeSettings _stored = new();

    private ProtocolNotices Create()
    {
        _settings.Get<ProtocolNoticeSettings>(Arg.Any<string?>()).Returns(_ => _stored);
        _settings.Update(Arg.Any<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>(), Arg.Any<string?>())
            .Returns(call => _stored = call.Arg<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>()(_stored));
        return new ProtocolNotices(_settings, _events);
    }

    private static CliMessages.ProtocolHandlerHeldElsewhere Notice(string scheme = "ror2mm", string holder = "r2modman.desktop")
        => new(scheme, holder);

    [Fact]
    public void ReportingAHolder_PublishesItAndKeepsItPending()
    {
        var sut = Create();

        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        _events.Received(1).Send(Notice());
        sut.Pending.Should().Equal(Notice());
    }

    [Fact]
    public void ReportingAHolder_DoesNotMarkItReported()
    {
        var sut = Create();

        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        _stored.LastReportedHolder.Should().BeEmpty();
    }

    [Fact]
    public void ReportingTheSameHolderTwice_KeepsOnePendingNotice()
    {
        var sut = Create();

        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        sut.Pending.Should().Equal(Notice());
    }

    [Fact]
    public void ANewHolderForTheSameScheme_ReplacesThePendingNotice()
    {
        var sut = Create();

        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.ReportHeldElsewhere("ror2mm", "gale.desktop");

        sut.Pending.Should().Equal(Notice(holder: "gale.desktop"));
    }

    [Fact]
    public void AcknowledgingANotice_SavesTheHolderAndClearsItFromPending()
    {
        var sut = Create();
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        sut.Acknowledge(Notice());

        _stored.LastReportedHolder.Should().Contain("ror2mm", "r2modman.desktop");
        sut.Pending.Should().BeEmpty();
    }

    [Fact]
    public void AnAcknowledgedHolder_IsNotReportedAgain()
    {
        var sut = Create();
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.Acknowledge(Notice());
        _events.ClearReceivedCalls();

        var later = Create();
        later.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        _events.DidNotReceive().Send(Arg.Any<CliMessages.ProtocolHandlerHeldElsewhere>());
        later.Pending.Should().BeEmpty();
    }

    [Fact]
    public void AHolderNobodyAcknowledged_IsReportedAgainOnTheNextStart()
    {
        Create().ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        _events.ClearReceivedCalls();

        var later = Create();
        later.ReportHeldElsewhere("ror2mm", "r2modman.desktop");

        _events.Received(1).Send(Notice());
    }

    [Fact]
    public void ADifferentHolderThanTheAcknowledgedOne_IsReported()
    {
        var sut = Create();
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.Acknowledge(Notice());
        _events.ClearReceivedCalls();

        sut.ReportHeldElsewhere("ror2mm", "gale.desktop");

        _events.Received(1).Send(Notice(holder: "gale.desktop"));
    }

    [Fact]
    public void ForgettingAScheme_ClearsItsSavedHolderAndPendingNotice()
    {
        var sut = Create();
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.Acknowledge(Notice());
        sut.ReportHeldElsewhere("ror2mm", "gale.desktop");
        sut.ReportHeldElsewhere("nxm", "vortex.desktop");

        sut.Forget("ror2mm");

        _stored.LastReportedHolder.Should().NotContainKey("ror2mm");
        sut.Pending.Should().Equal(Notice("nxm", "vortex.desktop"));
    }

    [Fact]
    public void ForgettingAScheme_KeepsTheOtherSchemesSavedHolders()
    {
        var sut = Create();
        sut.ReportHeldElsewhere("ror2mm", "r2modman.desktop");
        sut.Acknowledge(Notice());
        sut.ReportHeldElsewhere("nxm", "vortex.desktop");
        sut.Acknowledge(Notice("nxm", "vortex.desktop"));

        sut.Forget("ror2mm");

        _stored.LastReportedHolder.Should().Contain("nxm", "vortex.desktop");
    }

    [Fact]
    public void ForgettingAnUnknownScheme_WritesNothing()
    {
        var sut = Create();

        sut.Forget("ror2mm");

        _settings.DidNotReceive().Update(Arg.Any<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>(), Arg.Any<string?>());
    }
}

/// <summary>
/// The associate verbs run in a process without <see cref="ProtocolRegistration"/>, so they forget the notice themselves.
/// </summary>
public class ProtocolVerbsAssociateTests
{
    private readonly IEventBus _events = Substitute.For<IEventBus>();
    private readonly ISettingsManager _settings = Substitute.For<ISettingsManager>();
    private readonly IOSInterop _os = Substitute.For<IOSInterop>();
    private ProtocolNoticeSettings _stored = new() { LastReportedHolder = new Dictionary<string, string> { ["nxm"] = "vortex.desktop", ["ror2mm"] = "r2modman.desktop" } };

    private ProtocolNotices Create()
    {
        _settings.Get<ProtocolNoticeSettings>(Arg.Any<string?>()).Returns(_ => _stored);
        _settings.Update(Arg.Any<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>(), Arg.Any<string?>())
            .Returns(call => _stored = call.Arg<Func<ProtocolNoticeSettings, ProtocolNoticeSettings>>()(_stored));
        return new ProtocolNotices(_settings, _events);
    }

    [Fact]
    public async Task ATakeThatSucceeds_TakesWithTheTakeClaimAndForgetsTheHolder()
    {
        _os.RegisterUriSchemeHandler("nxm", UriSchemeClaim.Take, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UriSchemeRegistration>(new UriSchemeRegistration(true, "vortex.desktop")));

        var result = await ProtocolVerbs.Associate("nxm", _os, Create(), CancellationToken.None);

        result.Registered.Should().BeTrue();
        _stored.LastReportedHolder.Should().NotContainKey("nxm");
        _stored.LastReportedHolder.Should().ContainKey("ror2mm");
    }

    [Fact]
    public async Task ATakeThatFails_KeepsTheHolder()
    {
        _os.RegisterUriSchemeHandler("nxm", UriSchemeClaim.Take, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UriSchemeRegistration>(new UriSchemeRegistration(false, "vortex.desktop")));

        await ProtocolVerbs.Associate("nxm", _os, Create(), CancellationToken.None);

        _stored.LastReportedHolder.Should().ContainKey("nxm");
    }
}
