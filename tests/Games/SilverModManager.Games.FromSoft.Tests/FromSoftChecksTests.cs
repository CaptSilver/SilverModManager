using FluentAssertions;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Tests;

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

    // An offline game with the given rules, so a result does not depend on Elden Ring's own values.
    private static FromSoftGameDefinition Offline(GameFolderCheckRules rules, string? regulation = "regulation.bin")
        => TestSoulsDefinition.Definition with
        {
            CheckRules = rules,
            HasOnlinePlay = false,
            Formats = TestSoulsDefinition.Definition.Formats with { RegulationFileName = regulation },
        };

    private static FromSoftGameDefinition Online(GameFolderCheckRules rules, string? regulation = "regulation.bin")
        => Offline(rules, regulation) with { HasOnlinePlay = true };

    [Fact]
    public void RulesWithoutALauncherOrSpawnersOrExtraFoldersFindNothingToReport()
    {
        var results = FromSoftChecks.GameFolder(Folder(), Offline(GameFolderCheckRules.ModEngine2));

        results.Should().ContainSingle().Which.Should().Match<CheckResult>(r => r.Id == "artifacts" && r.Level == CheckLevel.Ok);
    }

    [Fact]
    public void ModEngine2RulesStillFindTheLoaderFilesAndTheModRegulation()
    {
        var game = Folder();
        Write(game / "modengine2.dll");
        Write(game / "mod/regulation.bin");

        var artifacts = FromSoftChecks.GameFolder(game, Offline(GameFolderCheckRules.ModEngine2)).Should().ContainSingle(r => r.Id == "artifacts").Subject;

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

        var results = FromSoftChecks.GameFolder(game, Offline(rules));

        results.Should().ContainSingle(r => r.Id == "artifacts").Which.Message.Should().Contain("other_launcher.exe.old");
        results.Should().NotContain(r => r.Id == "eac");

        (game / "other_launcher.exe.old").Delete();
        FromSoftChecks.GameFolder(game, Offline(rules)).Should().ContainSingle(r => r.Id == "eac").Which.Message.Should().Be("EAC state: armed");

        Write(game / "dinput8.dll");
        FromSoftChecks.GameFolder(game, Offline(rules)).Should().ContainSingle(r => r.Id == "eac").Which.Message.Should().Be("EAC state: disarmed");
    }

    [Fact]
    public void AProxyDllTheRulesDoNotListAsALoaderFileDoesNotDisarmTheLauncher()
    {
        var game = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/OTHER/Game");
        game.CreateDirectory();
        Write(game / "other_launcher.exe");
        Write(game / "dinput8.dll");
        var rules = GameFolderCheckRules.ModEngine2 with { Launcher = "other_launcher.exe", ModLoaderFiles = ["modengine2.dll"] };

        var results = FromSoftChecks.GameFolder(game, Offline(rules));

        // The artifacts check does not call dinput8.dll a mod file for these rules, so the EAC line cannot either.
        results.Should().ContainSingle(r => r.Id == "eac").Which.Message.Should().Be("EAC state: armed");
    }

    [Fact]
    public void EldenRingsRulesGiveTheLauncherArtifactsAndSpawnerResults()
    {
        var results = FromSoftChecks.GameFolder(Folder(), EldenRingGame.Definition);

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

    [Fact]
    public void ModArtifactsNameTheSameFilesTheArtifactsResultReports()
    {
        var game = Folder();
        Write(game / "dinput8.dll");
        Write(game / "mod/regulation.bin");

        var names = FromSoftChecks.ModArtifacts(game, EldenRingGame.Definition);

        names.Should().BeEquivalentTo("dinput8.dll", "mod/regulation.bin", "ersc_launcher.exe", "SeamlessCoop/");
        var message = FromSoftChecks.GameFolder(game, EldenRingGame.Definition).Single(r => r.Id == "artifacts").Message;
        names.Should().OnlyContain(name => message.Contains(name));
    }

    [Fact]
    public void ModArtifactsOfAMissingFolderAreEmpty()
    {
        FromSoftChecks.ModArtifacts(new InMemoryFileSystem().FromUnsanitizedFullPath("/nowhere/Game"), EldenRingGame.Definition).Should().BeEmpty();
    }

    [Fact]
    public void LoaderFilesInAnOnlineGameWithoutALauncherFail()
    {
        var game = Folder();
        (game / "start_protected_game.exe").Delete();
        Write(game / "dinput8.dll");

        var artifacts = FromSoftChecks.GameFolder(game, Online(GameFolderCheckRules.ModEngine2)).Should().ContainSingle(r => r.Id == "artifacts").Subject;

        artifacts.Level.Should().Be(CheckLevel.Fail);
        artifacts.Message.Should().Contain("dinput8.dll").And.Contain("no anti-cheat gate");
    }

    [Fact]
    public void LoaderFilesInAnOfflineGameWithoutALauncherOnlyWarn()
    {
        var game = Folder();
        Write(game / "dinput8.dll");

        FromSoftChecks.GameFolder(game, Offline(GameFolderCheckRules.ModEngine2)).Should().ContainSingle(r => r.Id == "artifacts").Which.Level.Should().Be(CheckLevel.Warn);
    }

    [Fact]
    public void TheModFolderRegulationIsCountedUnderTheGamesOwnName()
    {
        var game = Folder();
        Write(game / "mod/Data0.bdt");
        Write(game / "mod/regulation.bin");

        FromSoftChecks.ModArtifacts(game, Offline(GameFolderCheckRules.ModEngine2, "Data0.bdt")).Should().Equal("mod/Data0.bdt");
    }

    [Fact]
    public void NoRegulationMeansNoModFolderRegulationArtifact()
    {
        var game = Folder();
        Write(game / "mod/regulation.bin");

        FromSoftChecks.ModArtifacts(game, Offline(GameFolderCheckRules.ModEngine2, null)).Should().BeEmpty();
    }

    [Fact]
    public void NoEacLineWithoutALauncher()
    {
        FromSoftChecks.GameFolder(Folder(), Online(GameFolderCheckRules.ModEngine2)).Should().NotContain(r => r.Id == "eac");
    }
}
