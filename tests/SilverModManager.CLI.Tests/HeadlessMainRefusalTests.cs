using FluentAssertions;
using SilverModManager.App;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.SingleProcess;
using SilverModManager.SingleProcess.Exceptions;

namespace SilverModManager.CLI.Tests;

public class HeadlessMainRefusalTests : IDisposable
{
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-refusal-").FullName;
    private readonly List<InstallationLock> _held = [];

    public void Dispose()
    {
        foreach (var held in _held) held.Dispose();
        Directory.Delete(_runtimeDir, recursive: true);
    }

    private void WriteState(string storeId, int pid, string loadout, LaunchPhase phase)
    {
        // Held, like a live launch: a free lock would make the file a leftover.
        var path = InstallationLock.PathFor(_runtimeDir, "steam", storeId);
        var installationLock = new InstallationLock(path);
        installationLock.TryAcquire().Should().BeTrue();
        installationLock.WriteState(pid, loadout, phase, "");
        _held.Add(installationLock);
    }

    [Theory]
    [InlineData(LaunchPhase.Running, "is running", "Close the game")]
    [InlineData(LaunchPhase.Preparing, "is starting", "Close it once it is up")]
    public void ALiveLaunchIsNamedWithItsLoadoutAndPid(LaunchPhase phase, string state, string advice)
    {
        WriteState("489830", 4242, "Modded Skyrim", phase);

        var text = HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, _runtimeDir);

        text.Should().Contain("pid 4242").And.Contain("loadout 'Modded Skyrim'").And.Contain(state).And.Contain(advice);
    }

    [Fact]
    public void AFinishingLaunchIsStillCleaningUp()
    {
        WriteState("489830", 4242, "Modded Skyrim", LaunchPhase.Finishing);

        var text = HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, _runtimeDir);

        text.Should().Contain("still cleaning up").And.Contain("Modded Skyrim").And.Contain("4242");
        text.Should().NotContain("Close the game");
    }

    [Fact]
    public void TheStateOfAnotherProcessIsNotPickedUp()
    {
        WriteState("489830", 1111, "Someone Else's Loadout", LaunchPhase.Running);
        WriteState("1091500", 4242, "Cyberpunk Mods", LaunchPhase.Running);

        var text = HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, _runtimeDir);

        text.Should().Contain("Cyberpunk Mods").And.NotContain("Someone Else");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutAMatchingStateTheMessageNamesThePidAndOmitsTheLoadout(bool runtimeDirExists)
    {
        WriteState("489830", 1111, "Someone Else's Loadout", LaunchPhase.Running);
        var dir = runtimeDirExists ? _runtimeDir : Path.Combine(_runtimeDir, "missing");

        var text = HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, dir);

        text.Should().Be(new HeadlessMainRunning(MainKind.HeadlessRun, 4242).Message);
        text.Should().Contain("4242").And.NotContain("loadout");
    }

    [Fact]
    public void ACorruptStateFileOnlyCostsTheDetails()
    {
        var path = InstallationLock.PathFor(_runtimeDir, "steam", "1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.WriteAllText(Path.ChangeExtension(path, ".state"), "not a state file");

        HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, _runtimeDir)
            .Should().Be(new HeadlessMainRunning(MainKind.HeadlessRun, 4242).Message);
    }

    [Fact]
    public void ATerminalVerbsMainIsBusyWhateverTheLaunchStatesSay()
    {
        WriteState("489830", 4242, "Modded Skyrim", LaunchPhase.Running);

        HeadlessMainRefusal.Describe(MainKind.HeadlessCli, 4242, _runtimeDir)
            .Should().Be(new HeadlessMainRunning(MainKind.HeadlessCli, 4242).Message);
    }

    [Fact]
    public void WithoutARuntimeDirTheMessageNamesThePid()
    {
        HeadlessMainRefusal.Describe(MainKind.HeadlessRun, 4242, runtimeDir: null)
            .Should().Be(new HeadlessMainRunning(MainKind.HeadlessRun, 4242).Message);
    }
}
