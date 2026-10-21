using FluentAssertions;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests;

public class FromSoftChecksTests
{
    // A folder with Elden Ring's co-op folder, its co-op launcher, an anti-cheat launcher and a spawner DLL.
    private static AbsolutePath Folder()
    {
        var game = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/ELDEN RING/Game");
        game.CreateDirectory();
        Write(game / "eldenring.exe");
        Write(game / "start_protected_game.exe");
        Write(game / "ersc_launcher.exe");
        Write(game / "SeamlessCoop/ersc.dll");
        Write(game / "ermerchant.dll");
        return game;
    }

    private static void Write(AbsolutePath path)
    {
        path.Parent.CreateDirectory();
        using var stream = path.Create();
        stream.Write(new byte[1]);
    }

    [Fact]
    public void RulesWithoutALauncherOrSpawnersOrExtraFoldersFindNothingToReport()
    {
        var results = FromSoftChecks.GameFolder(Folder(), GameFolderCheckRules.ModEngine2);

        results.Should().ContainSingle().Which.Should().Match<CheckResult>(r => r.Id == "artifacts" && r.Level == CheckLevel.Ok);
    }

    [Fact]
    public void ModEngine2RulesStillFindTheLoaderFilesAndTheModRegulation()
    {
        var game = Folder();
        Write(game / "modengine2.dll");
        Write(game / "mod/regulation.bin");

        var artifacts = FromSoftChecks.GameFolder(game, GameFolderCheckRules.ModEngine2).Should().ContainSingle(r => r.Id == "artifacts").Subject;

        artifacts.Level.Should().Be(CheckLevel.Warn);
        artifacts.Message.Should().Contain("modengine2.dll").And.Contain("mod/regulation.bin");
    }

    [Fact]
    public void ARulesLauncherIsWhatTheArmedStateAndTheBackupNameAreBuiltFrom()
    {
        var game = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/OTHER/Game");
        game.CreateDirectory();
        Write(game / "other_launcher.exe");
        Write(game / "other_launcher.exe.old");
        var rules = GameFolderCheckRules.ModEngine2 with { Launcher = "other_launcher.exe", LauncherBackupSuffix = ".old" };

        var results = FromSoftChecks.GameFolder(game, rules);

        results.Should().ContainSingle(r => r.Id == "artifacts").Which.Message.Should().Contain("other_launcher.exe.old");
        results.Should().NotContain(r => r.Id == "eac");

        (game / "other_launcher.exe.old").Delete();
        FromSoftChecks.GameFolder(game, rules).Should().ContainSingle(r => r.Id == "eac").Which.Message.Should().Be("EAC state: armed");

        Write(game / "dinput8.dll");
        FromSoftChecks.GameFolder(game, rules).Should().ContainSingle(r => r.Id == "eac").Which.Message.Should().Be("EAC state: disarmed");
    }

    [Fact]
    public void EldenRingsRulesGiveTheLauncherArtifactsAndSpawnerResults()
    {
        var results = FromSoftChecks.GameFolder(Folder(), EldenRingGame.Definition.CheckRules);

        results.Should().ContainSingle(r => r.Id == "artifacts").Which.Level.Should().Be(CheckLevel.Fail);
        results.Should().ContainSingle(r => r.Id == "eac").Which.Level.Should().Be(CheckLevel.Info);
        results.Should().ContainSingle(r => r.Id == "spawners").Which.Level.Should().Be(CheckLevel.Warn);
    }

    [Fact]
    public void TheMe3ResultOfTheLoaderIsThePureFunctionTheFacadeForwardsTo()
    {
        var binary = new InMemoryFileSystem().FromUnsanitizedFullPath("/me3/bin/me3");

        var result = Me3Loader.HostCheckResult(binary, executable: true, version: "me3 0.13.0");

        result.Should().Be(EldenRingChecks.Me3(binary, executable: true, version: "me3 0.13.0"));
        result.Id.Should().Be("me3");
    }
}
