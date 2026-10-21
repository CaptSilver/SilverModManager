using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class WrapperPassThroughTests : IDisposable
{
    private const int GuiPid = 4242;

    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-wrapper-").FullName;

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private static readonly string[] SteamCommand =
        ["/steam/reaper", "SteamLaunch", "AppId=1091500", "--", "proton", "waitforexitandrun", "/game/Game.exe"];

    private string LockPath(string appId = "1091500") => InstallationLock.PathFor(_runtimeDir, "steam", appId);

    private Process HoldWithState(int pid, LaunchPhase phase, string appId = "1091500", string wineDllOverrides = "")
    {
        var holder = TestHost.StartHolder(LockPath(appId), seconds: 30);
        new InstallationLock(LockPath(appId)).WriteState(pid, "my loadout", phase, wineDllOverrides);
        return holder;
    }

    [Fact]
    public void TryOpen_JoinsTheWindowsRunningLaunch_FromTheAppIdInTheCommand()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            var found = WrapperPassThrough.TryOpen(SteamCommand, steamAppId: null, _runtimeDir, GuiPid);

            found.Should().NotBeNull();
            using var joined = found!.Value.Lock;
            joined.LockPath.Should().Be(LockPath());
            joined.IsHeld.Should().BeFalse();
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public void TryOpen_PrefersTheSteamAppIdVariableOverTheCommand()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running, appId: "7");
        try
        {
            var found = WrapperPassThrough.TryOpen(SteamCommand, steamAppId: "7", _runtimeDir, GuiPid);

            found.Should().NotBeNull();
            using var joined = found!.Value.Lock;
            joined.LockPath.Should().Be(LockPath("7"));
        }
        finally { holder.Kill(); }
    }

    [Theory]
    [InlineData(LaunchPhase.Preparing)]
    [InlineData(LaunchPhase.Finishing)]
    public void TryOpen_RefusesAnyPhaseButRunning(LaunchPhase phase)
    {
        using var holder = HoldWithState(GuiPid, phase);
        try
        {
            WrapperPassThrough.TryOpen(SteamCommand, null, _runtimeDir, GuiPid).Should().BeNull();
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public void TryOpen_RefusesALockHeldByAnotherPid()
    {
        using var holder = HoldWithState(GuiPid + 1, LaunchPhase.Running);
        try
        {
            WrapperPassThrough.TryOpen(SteamCommand, null, _runtimeDir, GuiPid).Should().BeNull();
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public void TryOpen_RefusesAFreeLockWithALeftoverState()
    {
        using (var old = new InstallationLock(LockPath()))
        {
            old.TryAcquire().Should().BeTrue();
            old.WriteState(GuiPid, "my loadout", LaunchPhase.Running, "");
        }

        WrapperPassThrough.TryOpen(SteamCommand, null, _runtimeDir, GuiPid).Should().BeNull();
    }

    [Fact]
    public void TryOpen_RefusesWhenNothingHasEverLaunched()
    {
        WrapperPassThrough.TryOpen(SteamCommand, null, _runtimeDir, GuiPid).Should().BeNull();
    }

    [Fact]
    public void TryOpen_RefusesACommandWithoutAnAppId()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            WrapperPassThrough.TryOpen(["/game/Game.exe"], null, _runtimeDir, GuiPid).Should().BeNull();
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public void WriteWrapperKeyOnTheFoundLock_KeepsTheHoldersPhaseAndPid()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            using var found = WrapperPassThrough.TryOpen(SteamCommand, null, _runtimeDir, GuiPid)!.Value.Lock;
            found.WriteWrapperKey(LaunchSession.WrapperKey, "99");

            var state = found.ReadState()!;
            state.Pid.Should().Be(GuiPid);
            state.Phase.Should().Be(LaunchPhase.Running);
            state.Values[LaunchSession.WrapperKey].Should().Be("99");
        }
        finally { holder.Kill(); }
    }

    private static readonly IReadOnlyDictionary<string, string> BasicEnvironment =
        new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin" };

    private string File_(string name) => Path.Combine(_runtimeDir, name);

    private IEnvironmentReader SteamEnvironment(string? steamAppId = "1091500") => new FakeEnvironment(new Dictionary<string, string?>
    {
        ["XDG_RUNTIME_DIR"] = _runtimeDir,
        ["SteamAppId"] = steamAppId,
    });

    private Task<int?> TryRun(IReadOnlyList<string> command, IEnvironmentReader? environment = null) =>
        WrapperPassThrough.TryRunAsync(command, environment ?? SteamEnvironment(), GuiPid, NullLogger.Instance, BasicEnvironment);

    [Fact]
    public async Task TryRun_RunsTheCommandUnchangedAndReportsTheWrapper()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            string[] command = ["sh", "-c", "printf '%s|' \"$@\" > \"$0\"; exit 3", File_("argv"), "a b", "", "AppId=1", "--"];

            var exitCode = await TryRun(command);

            exitCode.Should().Be(3);
            (await File.ReadAllTextAsync(File_("argv"))).Should().Be("a b||AppId=1|--|");
            var state = new InstallationLock(LockPath()).ReadState()!;
            state.Pid.Should().Be(GuiPid);
            state.Phase.Should().Be(LaunchPhase.Running);
            state.Values[LaunchSession.WrapperKey].Should().Be(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task TryRun_SetsTheWineDllOverridesTheWindowRecorded()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running, wineDllOverrides: "winmm=n,b;version=n,b");
        try
        {
            var environment = new Dictionary<string, string>(BasicEnvironment) { ["WINEDLLOVERRIDES"] = "dxgi=n,b" };

            var exitCode = await WrapperPassThrough.TryRunAsync(
                ["sh", "-c", "printf '%s' \"$WINEDLLOVERRIDES\" > \"$0\"", File_("overrides")],
                SteamEnvironment(), GuiPid, NullLogger.Instance, environment);

            exitCode.Should().Be(0);
            (await File.ReadAllTextAsync(File_("overrides"))).Should().Be("dxgi=n,b;winmm=n,b;version=n,b");
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task TryRun_LeavesTheEnvironmentAloneWhenTheWindowRecordedNoOverrides()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            var exitCode = await TryRun(["sh", "-c", "printf '%s' \"${WINEDLLOVERRIDES-unset}\" > \"$0\"", File_("overrides")]);

            exitCode.Should().Be(0);
            (await File.ReadAllTextAsync(File_("overrides"))).Should().Be("unset");
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task TryRun_StartsNothingWhenTheWindowHasNoRunningLaunch()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Preparing);
        try
        {
            var exitCode = await TryRun(["sh", "-c", "touch \"$0\"", File_("ran")]);

            exitCode.Should().BeNull();
            File.Exists(File_("ran")).Should().BeFalse();
            new InstallationLock(LockPath()).ReadState()!.Values.Should().NotContainKey(LaunchSession.WrapperKey);
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task TryRun_LeavesNoWrapperKeyWhenTheCommandCannotStart()
    {
        // The window keeps waiting on Steam for as long as the key says a wrapper is on its way.
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            var run = () => TryRun([File_("no-such-program")]);

            await run.Should().ThrowAsync<Win32Exception>();
            new InstallationLock(LockPath()).ReadState()!.Values.Should().NotContainKey(LaunchSession.WrapperKey);
        }
        finally { holder.Kill(); }
    }

    [Fact]
    public async Task TryRun_ReadsTheAppIdFromTheCommandWhenSteamSetNone()
    {
        using var holder = HoldWithState(GuiPid, LaunchPhase.Running);
        try
        {
            string[] command = ["sh", "-c", "exit 0", "SteamLaunch", "AppId=1091500"];

            var exitCode = await TryRun(command, SteamEnvironment(steamAppId: null));

            exitCode.Should().Be(0);
        }
        finally { holder.Kill(); }
    }

    private sealed class FakeEnvironment(IReadOnlyDictionary<string, string?> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }
}
