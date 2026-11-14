using Avalonia.Controls;
using Avalonia;
using FluentAssertions;
using NSubstitute;
using R3;
using SilverModManager.App.UI.Input;
using SilverModManager.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Input;

// Switching the mode writes a resource on the one Application, which every window in the process reads.
[Collection(HeadlessUi.Collection)]
public class InputModeServiceTests(IServiceProvider provider) : AUiTest(provider)
{
    private static IGameModeDetector Detector(bool gameMode)
    {
        var detector = Substitute.For<IGameModeDetector>();
        detector.IsGameMode.Returns(gameMode);
        return detector;
    }

    private static bool PopupsInOverlay() => (bool)Application.Current!.FindResource("PopupsInOverlayLayer")!;

    [Fact]
    public void ItStartsInPointerMode()
    {
        new InputModeService(Detector(false)).Mode.Should().Be(InputMode.Pointer);
    }

    [Fact]
    public void APadEventSwitchesToPadModeAndAPointerMoveSwitchesBack()
    {
        var service = new InputModeService(Detector(false));
        var seen = new List<InputMode>();
        using var subscription = service.Modes.Subscribe(seen.Add);

        service.PadUsed();
        service.PadUsed();
        service.PointerMoved();

        service.Mode.Should().Be(InputMode.Pointer);
        seen.Should().Equal(InputMode.Pointer, InputMode.Pad, InputMode.Pointer);
    }

    [Fact]
    public void PopupsStayInTheOverlayLayerInGameModeEvenWithThePointer()
    {
        new InputModeService(Detector(true)).UsesOverlayPopups.Should().BeTrue();
        new InputModeService(Detector(false)).UsesOverlayPopups.Should().BeFalse();
    }

    [Fact]
    public async Task PadModeTurnsOnTheOverlayPopupResourceAndPointerModeTurnsItOff()
    {
        await OnUi(() =>
        {
            var service = new InputModeService(Detector(false));
            try
            {
                service.PadUsed();
                PopupsInOverlay().Should().BeTrue();

                service.PointerMoved();
                PopupsInOverlay().Should().BeFalse();
            }
            finally
            {
                service.PointerMoved();
            }
        });
    }
}
