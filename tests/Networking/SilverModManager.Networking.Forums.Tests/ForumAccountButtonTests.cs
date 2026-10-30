using FluentAssertions;
using NSubstitute;
using R3;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Networking.Forums.Tests;

public class ForumAccountButtonTests
{
    private readonly IForumAccounts _accounts = Substitute.For<IForumAccounts>();
    private readonly IForumSignInFlow _flow = Substitute.For<IForumSignInFlow>();
    private readonly Subject<ForumSite> _changed = new();

    public ForumAccountButtonTests()
    {
        _accounts.Changed.Returns(_changed);
        _accounts.StatusOf(Arg.Any<ForumSite>()).Returns(new ForumAccountStatus(ForumAccountState.SignedOut, ""));
    }

    private ForumAccountButton Button(ForumSite site = ForumSite.LoversLab) => new(_accounts, _flow, site);

    private void Status(ForumSite site, ForumAccountState state, string source = "")
        => _accounts.StatusOf(site).Returns(new ForumAccountStatus(state, source));

    [Theory]
    [InlineData(ForumSite.LoversLab, "LoversLab")]
    [InlineData(ForumSite.VectorPlexus, "VectorPlexus")]
    public void NoClientIdSaysSignInIsNotSetUpAndOffersNoButton(ForumSite site, string name)
    {
        Status(site, ForumAccountState.NotSetUp);

        Button(site).Current.Should().Be(new ActionButtonState($"Sign-in for {name} isn't set up yet. The site hasn't issued this app a client.", null));
    }

    [Fact]
    public void SignedOutOffersSignIn()
    {
        Button().Current.Should().Be(new ActionButtonState("Not signed in", "Sign in"));
    }

    [Fact]
    public void SignedInNamesWhereTheTokenIsKeptAndOffersSignOut()
    {
        Status(ForumSite.LoversLab, ForumAccountState.SignedIn, "Secret Service");

        Button().Current.Should().Be(new ActionButtonState("Signed in. Stored in Secret Service.", "Sign out"));
    }

    [Fact]
    public void SignedInWithoutAKnownSourceDoesNotInventOne()
    {
        Status(ForumSite.LoversLab, ForumAccountState.SignedIn);

        Button().Current.Text.Should().Be("Signed in.");
    }

    [Fact]
    public void AnExpiredSessionOffersSignInAgain()
    {
        Status(ForumSite.LoversLab, ForumAccountState.Expired);

        Button().Current.Should().Be(new ActionButtonState("Session expired. Sign in again.", "Sign in"));
    }

    [Fact]
    public void StateReplaysTheCurrentTextAndFollowsChangesOfItsOwnSiteOnly()
    {
        var button = Button();
        var seen = new List<ActionButtonState>();
        using var subscription = button.State.Subscribe(state => seen.Add(state));

        Status(ForumSite.LoversLab, ForumAccountState.SignedIn, "Secret Service");
        _changed.OnNext(ForumSite.VectorPlexus);
        _changed.OnNext(ForumSite.LoversLab);

        seen.Select(s => s.Text).Should().Equal("Not signed in", "Signed in. Stored in Secret Service.");
    }

    [Fact]
    public async Task InvokeWhileSignedOutRunsTheSignInForItsSite()
    {
        await Button(ForumSite.VectorPlexus).Invoke(CancellationToken.None);

        await _flow.Received(1).Run(ForumSite.VectorPlexus, Arg.Any<CancellationToken>());
        await _accounts.DidNotReceive().SignOut(Arg.Any<ForumSite>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeWhileSignedInSignsOut()
    {
        Status(ForumSite.LoversLab, ForumAccountState.SignedIn);

        await Button().Invoke(CancellationToken.None);

        await _accounts.Received(1).SignOut(ForumSite.LoversLab, Arg.Any<CancellationToken>());
        await _flow.DidNotReceive().Run(Arg.Any<ForumSite>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeWithoutAButtonIsRefusedNotIgnored()
    {
        Status(ForumSite.LoversLab, ForumAccountState.NotSetUp);

        await Button().Invoking(b => b.Invoke(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }
}
