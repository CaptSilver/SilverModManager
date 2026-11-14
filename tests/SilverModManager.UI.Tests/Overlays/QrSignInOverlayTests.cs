using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.SteamQr;

namespace SilverModManager.UI.Tests.Overlays;

public class QrSignInOverlayTests
{
    private static readonly Uri First = new("https://s.team/q/1/first");
    private static readonly Uri Second = new("https://s.team/q/1/second");

    private readonly IOverlayController _controller = Substitute.For<IOverlayController>();
    private readonly List<QrSignInOverlayViewModel> _shown = [];
    private readonly QrAuthInterventionHandler _handler;

    public QrSignInOverlayTests()
    {
        _controller.When(c => c.Enqueue(Arg.Any<IOverlayViewModel>())).Do(call =>
        {
            var overlay = (QrSignInOverlayViewModel)call.Arg<IOverlayViewModel>();
            overlay.Controller = _controller;
            _shown.Add(overlay);
        });

        // The UI thread is stood in for by the calling one: these tests are about what the handler does, not about Avalonia's dispatcher.
        _handler = new QrAuthInterventionHandler(_controller, action => action());
    }

    [Fact]
    public void ShowQrCodeOpensTheOverlayWithTheUri()
    {
        _handler.ShowQRCode(First, CancellationToken.None);

        var overlay = _shown.Should().ContainSingle().Subject;
        overlay.Uri.Should().Be(First);
        overlay.Image.Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "the code is a PNG picture of the uri");
    }

    [Fact]
    public void CancellingTheTokenClosesIt()
    {
        using var source = new CancellationTokenSource();
        _handler.ShowQRCode(First, source.Token);
        var overlay = _shown.Single();

        source.Cancel();

        overlay.CompletionTask.IsCompleted.Should().BeTrue();
        _controller.Received(1).Remove(overlay);
    }

    [Fact]
    public void ARefreshedCodeOfTheSameSignInChangesThePictureInPlace()
    {
        using var source = new CancellationTokenSource();
        _handler.ShowQRCode(First, source.Token);
        var overlay = _shown.Single();
        var before = overlay.Image;

        _handler.ShowQRCode(Second, source.Token);

        _shown.Should().ContainSingle("a second overlay would stack a stale code under the fresh one");
        overlay.Uri.Should().Be(Second);
        overlay.Image.Should().NotEqual(before);
    }

    [Fact]
    public void ASecondSignInReplacesTheFirstOverlay()
    {
        using var one = new CancellationTokenSource();
        using var two = new CancellationTokenSource();
        _handler.ShowQRCode(First, one.Token);

        _handler.ShowQRCode(Second, two.Token);

        _shown.Should().HaveCount(2);
        _shown[0].CompletionTask.IsCompleted.Should().BeTrue();
        _shown[1].CompletionTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void ACodeShownForAnAlreadyCancelledSignInOpensNothing()
    {
        _handler.ShowQRCode(First, new CancellationToken(canceled: true));

        _shown.Should().BeEmpty();
    }

    [Fact]
    public void AHiddenOverlayStaysHiddenWhenTheCodeRefreshes()
    {
        using var source = new CancellationTokenSource();
        _handler.ShowQRCode(First, source.Token);
        _shown.Single().Hide.Execute(R3.Unit.Default);

        _handler.ShowQRCode(Second, source.Token);

        _shown.Should().ContainSingle("the person hid the code and is not shown it again for every fresh one");
        _shown[0].CompletionTask.IsCompleted.Should().BeTrue();
    }
}
