using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.UI.Sdk;

namespace SilverModManager.UI.Tests.Overlays;

public class ForumSignInFlowTests
{
    private readonly IForumAccounts _accounts = Substitute.For<IForumAccounts>();
    private readonly IWindowNotificationService _toasts = Substitute.For<IWindowNotificationService>();
    private readonly IErrorPresenter _errors = Substitute.For<IErrorPresenter>();

    private ForumSignInFlow Flow() => new(_accounts, _toasts, _errors, NullLogger<ForumSignInFlow>.Instance);

    [Fact]
    public async Task ASignInThatSucceedsSaysNothing()
    {
        await Flow().Run(ForumSite.LoversLab, CancellationToken.None);

        await _accounts.Received(1).SignIn(ForumSite.LoversLab, Arg.Any<CancellationToken>());
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
        await _errors.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }

    [Fact]
    public async Task ADeclinedSignInIsAToastNotADialog()
    {
        _accounts.SignIn(ForumSite.LoversLab, Arg.Any<CancellationToken>()).Returns(Task.FromException(new SignInDeclinedException("LoversLab")));

        await Flow().Run(ForumSite.LoversLab, CancellationToken.None);

        _toasts.Received(1).ShowToast("Sign-in to LoversLab was declined", ToastNotificationVariant.Failure);
        await _errors.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }

    [Fact]
    public async Task ATimedOutSignInIsAToastNotADialog()
    {
        _accounts.SignIn(ForumSite.VectorPlexus, Arg.Any<CancellationToken>()).Returns(Task.FromException(new SignInTimedOutException("VectorPlexus")));

        await Flow().Run(ForumSite.VectorPlexus, CancellationToken.None);

        _toasts.Received(1).ShowToast("Sign-in to VectorPlexus timed out", ToastNotificationVariant.Failure);
        await _errors.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }

    [Fact]
    public async Task AnyOtherFailureGoesToTheErrorPresenter()
    {
        var refusal = new SignInNotSetUpException("LoversLab");
        _accounts.SignIn(ForumSite.LoversLab, Arg.Any<CancellationToken>()).Returns(Task.FromException(refusal));

        await Flow().Run(ForumSite.LoversLab, CancellationToken.None);

        await _errors.Received(1).Report(refusal, "signing in to LoversLab");
        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }

    [Fact]
    public async Task CancellingSaysNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _accounts.SignIn(ForumSite.LoversLab, Arg.Any<CancellationToken>()).Returns(Task.FromException(new OperationCanceledException(cts.Token)));

        await Flow().Run(ForumSite.LoversLab, cts.Token);

        _toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
        await _errors.DidNotReceiveWithAnyArgs().Report(default!, default!);
    }
}
