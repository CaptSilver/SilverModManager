using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using R3;
using SDL;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Platform.Gamepad.Tests;

public class SdlGamepadSourceTests
{
    private const int South = (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH;
    private readonly FakeSdlEvents _sdl = new();
    private readonly FakeTimeProvider _time = new();

    private static async Task<T> Next<T>(IEnumerable<GamepadEvent> seen) where T : GamepadEvent
    {
        for (var i = 0; i < 200; i++)
        {
            var hit = seen.ToArray().OfType<T>().FirstOrDefault();
            if (hit is not null) return hit;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No {typeof(T).Name} arrived");
    }

    private (SdlGamepadSource Source, List<GamepadEvent> Seen) Started()
    {
        var source = new SdlGamepadSource(_sdl, _time);
        var seen = new List<GamepadEvent>();
        source.Events.Subscribe(e => { lock (seen) seen.Add(e); });
        source.Start();
        return (source, seen);
    }

    [Fact]
    public async Task AButtonDownIsMappedToOurEnum()
    {
        var (source, seen) = Started();
        using var _ = source;

        _sdl.Queue(new SdlEventView(SdlEventKind.ButtonDown, 1, South));

        (await Next<ButtonDown>(seen)).Button.Should().Be(GamepadButton.A);
    }

    [Theory]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, GamepadButton.B)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, GamepadButton.X)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, GamepadButton.Y)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, GamepadButton.LB)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, GamepadButton.RB)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, GamepadButton.DPadUp)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, GamepadButton.DPadDown)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, GamepadButton.DPadLeft)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, GamepadButton.DPadRight)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, GamepadButton.View)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, GamepadButton.Menu)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, GamepadButton.LeftStick)]
    [InlineData(SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, GamepadButton.RightStick)]
    public void EverySdlButtonHasOneOfOurs(SDL_GamepadButton sdl, GamepadButton ours)
    {
        SdlGamepadSource.ButtonFor((int)sdl).Should().Be(ours);
    }

    [Fact]
    public void AButtonWithNoCounterpartIsIgnored()
    {
        SdlGamepadSource.ButtonFor((int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE).Should().BeNull();
    }

    [Fact]
    public async Task AnAddedPadIsOpenedAndReported()
    {
        var (source, seen) = Started();
        using var _ = source;

        _sdl.Queue(new SdlEventView(SdlEventKind.PadAdded, 7));

        var added = await Next<PadAdded>(seen);
        added.Name.Should().Be("Xbox");
        added.Type.Should().Be(GamepadType.Xbox);
        _sdl.Opened.Should().Equal(7u);
        source.Pads.Should().ContainSingle().Which.Name.Should().Be("Xbox");
    }

    [Fact]
    public async Task ARemovedPadIsClosedAndReported()
    {
        var (source, seen) = Started();
        using var _ = source;
        _sdl.Queue(new SdlEventView(SdlEventKind.PadAdded, 7));
        await Next<PadAdded>(seen);

        _sdl.Queue(new SdlEventView(SdlEventKind.PadRemoved, 7));

        (await Next<PadRemoved>(seen)).Name.Should().Be("Xbox");
        _sdl.Closed.Should().Equal(1007);
        source.Pads.Should().BeEmpty();
    }

    [Fact]
    public async Task PausedSourcesDropEvents()
    {
        var (source, seen) = Started();
        using var _ = source;
        source.Pause(true);

        _sdl.Queue(new SdlEventView(SdlEventKind.ButtonDown, 1, South));
        _sdl.Queue(new SdlEventView(SdlEventKind.PadAdded, 7));
        await Next<PadAdded>(seen);
        lock (seen) seen.OfType<ButtonDown>().Should().BeEmpty("a pad used while the window is inactive does nothing");

        source.Pause(false);
        _sdl.Queue(new SdlEventView(SdlEventKind.ButtonDown, 1, South));

        (await Next<ButtonDown>(seen)).Button.Should().Be(GamepadButton.A);
    }

    [Fact]
    public async Task APastTheThresholdTriggerIsAButtonPressUntilItReleases()
    {
        var (source, seen) = Started();
        using var _ = source;

        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 30000));
        (await Next<ButtonDown>(seen)).Button.Should().Be(GamepadButton.RT);

        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 20000));
        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER, 0));
        (await Next<ButtonUp>(seen)).Button.Should().Be(GamepadButton.RT);
        lock (seen) seen.OfType<ButtonDown>().Should().ContainSingle("a trigger between the thresholds does not press again");
    }

    [Fact]
    public async Task ATriggerHeldThroughAPauseCanBePressedAgain()
    {
        var (source, seen) = Started();
        using var _ = source;
        var rt = (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER;
        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, rt, 30000));
        await Next<ButtonDown>(seen);

        // The release happens while paused and is dropped, so the source must forget the trigger was down.
        source.Pause(true);
        source.Pause(false);
        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, rt, 30000));

        for (var i = 0; i < 200; i++)
        {
            lock (seen) if (seen.OfType<ButtonDown>().Count() == 2) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The second press never arrived");
    }

    [Fact]
    public async Task TheLeftStickAlsoSteersAsDPadPresses()
    {
        var (source, seen) = Started();
        using var _ = source;

        _sdl.Queue(new SdlEventView(SdlEventKind.Axis, 1, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, 30000));

        (await Next<ButtonDown>(seen)).Button.Should().Be(GamepadButton.DPadRight);
        (await Next<Stick>(seen)).Side.Should().Be(StickSide.Left);
    }

    [Fact]
    public async Task StoppingEndsTheReadThread()
    {
        var (source, _) = Started();

        var stop = Task.Run(source.Stop, TestContext.Current.CancellationToken);

        (await Task.WhenAny(stop, Task.Delay(5000, TestContext.Current.CancellationToken))).Should().BeSameAs(stop);
        source.Dispose();
    }

    [Fact]
    public void TheFactoryFallsBackToTheNullSourceWhenSdlCannotLoad()
    {
        var sdl = new FakeSdlEvents { InitThrows = new DllNotFoundException("libSDL3.so.0: cannot open shared object file") };

        var source = GamepadSourceFactory.Create(NullLogger.Instance, enabled: true, sdl, _time);

        source.Should().BeOfType<NullGamepadSource>().Which.DisabledReason.Should().Contain("libSDL3");
    }

    [Fact]
    public void TheFactoryFallsBackWhenSdlRefusesToStart()
    {
        var sdl = new FakeSdlEvents { InitResult = false, LastError = "No gamepad subsystem" };

        var source = GamepadSourceFactory.Create(NullLogger.Instance, enabled: true, sdl, _time);

        source.Should().BeOfType<NullGamepadSource>().Which.DisabledReason.Should().Contain("No gamepad subsystem");
    }

    [Fact]
    public void TheFactoryReturnsTheNullSourceWhenControllerIsOff()
    {
        var source = GamepadSourceFactory.Create(NullLogger.Instance, enabled: false, _sdl, _time);

        source.Should().BeOfType<NullGamepadSource>().Which.DisabledReason.Should().Be("Controller is off");
    }

    [Fact]
    public void TheFactoryReturnsTheSdlSourceWhenItLoads()
    {
        var source = GamepadSourceFactory.Create(NullLogger.Instance, enabled: true, _sdl, _time);

        source.Should().BeOfType<SdlGamepadSource>().Which.DisabledReason.Should().BeNull();
    }
}

public class SdlNativeLoaderTests
{
    // SDL_GetVersion needs no SDL_Init, so this loads the library without opening any pad.
    [Fact]
    public void TheBundledLibraryLoadsAndReportsAVersion()
    {
        // The build machine may also have a system libSDL3, which would load through the fallback and hide a broken
        // bundled path, so check the bundled file is in the output. A build with no RuntimeIdentifier keeps it under
        // runtimes/<rid>/native; a RID-specific one (publish, the arm64 runs) puts it beside the assemblies.
        var rid = BundledNativeLibrary.RuntimeIdentifiers()[^1];
        (File.Exists(Path.Combine(AppContext.BaseDirectory, BundledNativeLibrary.RelativePath(rid, "libSDL3.so")))
         || File.Exists(Path.Combine(AppContext.BaseDirectory, "libSDL3.so")))
            .Should().BeTrue($"the package ships libSDL3 for {rid} next to the app");
        SdlNativeLoader.Install();

        new SdlEvents().Version.Should().MatchRegex(@"^3\.\d+\.\d+$");
    }
}
