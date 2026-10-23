using FluentAssertions;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests;

public class EldenRingChecksTests
{
    private static AbsolutePath Folder(Action<AbsolutePath>? populate = null, bool launcher = true)
    {
        var game = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/ELDEN RING/Game");
        game.CreateDirectory();
        Write(game / "eldenring.exe", 1);
        if (launcher) Write(game / "start_protected_game.exe", 16);
        populate?.Invoke(game);
        return game;
    }

    private static void Write(AbsolutePath path, int size)
    {
        path.Parent.CreateDirectory();
        using var stream = path.Create();
        stream.Write(new byte[size]);
    }

    private static CheckResult Single(IEnumerable<CheckResult> results, string id) => results.Should().ContainSingle(r => r.Id == id).Subject;

    [Fact]
    public void AProxyDllBesideTheLauncherFails()
    {
        var game = Folder(g => Write(g / "dinput8.dll", 1));

        var artifacts = Single(EldenRingChecks.GameFolder(game), "artifacts");

        artifacts.Level.Should().Be(CheckLevel.Fail);
        artifacts.Message.Should().Contain("dinput8.dll").And.Contain("online vanilla launch");
    }

    [Fact]
    public void ArtifactsWithoutTheLauncherWarnAndNameEveryFile()
    {
        var game = Folder(g =>
        {
            Write(g / "dinput8.dll", 1);
            Write(g / "modengine.toml", 1);
        }, launcher: false);

        var artifacts = Single(EldenRingChecks.GameFolder(game), "artifacts");

        artifacts.Level.Should().Be(CheckLevel.Warn);
        artifacts.Message.Should().Contain("dinput8.dll").And.Contain("modengine.toml");
    }

    [Fact]
    public void AModFolderRegulationFailsBesideTheLauncher()
    {
        var game = Folder(g => Write(g / "mod/regulation.bin", 1));

        var artifacts = Single(EldenRingChecks.GameFolder(game), "artifacts");

        artifacts.Level.Should().Be(CheckLevel.Fail);
        artifacts.Message.Should().Contain("mod/regulation.bin");
    }

    [Fact]
    public void AGameFolderWithOnlyVanillaFilesIsClean()
    {
        var results = EldenRingChecks.GameFolder(Folder());

        Single(results, "artifacts").Level.Should().Be(CheckLevel.Ok);
        results.Should().NotContain(r => r.Level == CheckLevel.Warn || r.Level == CheckLevel.Fail);
    }

    [Fact]
    public void ASpawnerIsFoundWhateverTheCaseAndHowDeepItIs()
    {
        var game = Folder(g => Write(g / "mods/Glorious_Merchant.DLL", 1));

        var spawners = EldenRingChecks.GameFolder(game).Where(r => r.Id == "spawners").ToArray();

        spawners.Should().ContainSingle().Which.Level.Should().Be(CheckLevel.Warn);
        spawners[0].Message.Should().Contain("Glorious_Merchant.DLL");
    }

    [Fact]
    public void EachSpawnerGetsItsOwnWarning()
    {
        var game = Folder(g =>
        {
            Write(g / "ermerchant.dll", 1);
            Write(g / "ErdTools.dll", 1);
        });

        EldenRingChecks.GameFolder(game).Where(r => r.Id == "spawners").Should().HaveCount(2).And.OnlyContain(r => r.Level == CheckLevel.Warn);
    }

    [Fact]
    public void AnOldLauncherSwapFailsAndPointsAtTheCleanup()
    {
        var game = Folder(g => Write(g / "start_protected_game.exe.erm-backup", 1));

        var artifacts = Single(EldenRingChecks.GameFolder(game), "artifacts");

        artifacts.Level.Should().Be(CheckLevel.Fail);
        artifacts.Message.Should().Contain("launcher swap").And.Contain("cleanup");
    }

    [Fact]
    public void AMissingGameFolderIsReportedNotThrown()
    {
        var missing = new InMemoryFileSystem().FromUnsanitizedFullPath("/nowhere/Game");

        var artifacts = Single(EldenRingChecks.GameFolder(missing), "artifacts");

        artifacts.Level.Should().Be(CheckLevel.Warn);
        artifacts.Message.Should().Contain("/nowhere/Game");
    }

    [Theory]
    [InlineData(false, false, "armed")]
    [InlineData(false, true, "disarmed")]
    [InlineData(true, false, "absent")]
    public void TheAntiCheatStateUsesTheStateNamesOfTheOldTool(bool noLauncher, bool proxy, string expected)
    {
        var game = Folder(g => { if (proxy) Write(g / "winhttp.dll", 1); }, launcher: !noLauncher);

        var eac = Single(EldenRingChecks.GameFolder(game), "eac");

        eac.Level.Should().Be(CheckLevel.Info);
        eac.Message.Should().Be($"EAC state: {expected}");
    }

    [Fact]
    public void AHardenedInstallIsReportedByTheArtifactsCheckAlone()
    {
        var game = Folder(g => Write(g / "start_protected_game.exe.erm-backup", 1));

        EldenRingChecks.GameFolder(game).Should().NotContain(r => r.Id == "eac");
    }

    // Each row is one case of the old tool's doctor, the scenario that triggers it, and what this app reports for the
    // same folder. A row with no app check says why there is none.
    public static TheoryData<string, string, string?, CheckLevel, string> VerdictMapping => new()
    {
        { "proxy beside the launcher", "EAC disarmed + mod artifacts", "artifacts", CheckLevel.Fail, "same" },
        { "artifacts, no launcher", "mod artifacts present (EAC absent)", "artifacts", CheckLevel.Warn, "same" },
        { "mod/regulation.bin beside the launcher", "EAC armed + mod artifacts", "artifacts", CheckLevel.Fail, "same" },
        { "vanilla files only", "no proxy DLL / ModEngine artifacts", "artifacts", CheckLevel.Ok, "same" },
        { "EAC armed", "EAC state: armed", "eac", CheckLevel.Info, "same" },
        { "EAC disarmed", "EAC state: disarmed", "eac", CheckLevel.Info, "same" },
        { "EAC absent", "EAC state: absent", "eac", CheckLevel.Info, "same" },
        { "spawner in a subfolder", "item-spawner mod present", "spawners", CheckLevel.Warn, "same" },
        { "hardened", "EAC state: hardened (ok)", "artifacts", CheckLevel.Fail, "inverted on purpose: the game folder is not pristine" },
        { "Seamless Co-op launcher only", "Seamless Co-op launcher present (ok)", "artifacts", CheckLevel.Fail, "inverted on purpose: part of the artifacts, and the launcher is present" },
        { "SeamlessCoop folder only", "(not checked)", "artifacts", CheckLevel.Fail, "new here" },
        { "mods folder only", "(not checked)", "artifacts", CheckLevel.Fail, "new here" },
        { "stale launcher", "launcher is stale", null, CheckLevel.Ok, "dropped: only meaningful on a hardened install" },
    };

    private static AbsolutePath Scenario(string name) => name switch
    {
        "proxy beside the launcher" => Folder(g => Write(g / "dinput8.dll", 1)),
        "artifacts, no launcher" => Folder(g => { Write(g / "dinput8.dll", 1); Write(g / "modengine.toml", 1); }, launcher: false),
        "mod/regulation.bin beside the launcher" => Folder(g => Write(g / "mod/regulation.bin", 1)),
        "vanilla files only" => Folder(),
        "EAC armed" => Folder(),
        "EAC disarmed" => Folder(g => Write(g / "dinput8.dll", 1)),
        "EAC absent" => Folder(launcher: false),
        "spawner in a subfolder" => Folder(g => Write(g / "mods/erdtools.dll", 1)),
        "hardened" => Folder(g => Write(g / "start_protected_game.exe.erm-backup", 1)),
        "Seamless Co-op launcher only" => Folder(g => Write(g / "ersc_launcher.exe", 1)),
        "SeamlessCoop folder only" => Folder(g => Write(g / "SeamlessCoop/ersc.dll", 1)),
        "mods folder only" => Folder(g => Write(g / "mods/readme.txt", 1)),
        "stale launcher" => Folder(g =>
        {
            Write(g / "start_protected_game.exe.erm-backup", 1);
            Write(g / "eldenring.exe", 2);
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    [Theory]
    [MemberData(nameof(VerdictMapping))]
    public void TheVerdictForAFolderMatchesTheMapping(string scenario, string oldTool, string? checkId, CheckLevel expected, string relation)
    {
        var results = EldenRingChecks.GameFolder(Scenario(scenario));

        if (checkId is null)
        {
            results.Select(r => r.Id).Distinct().Should().BeEquivalentTo(["artifacts"], $"{oldTool} is {relation}");
            return;
        }

        results.Where(r => r.Id == checkId).Max(r => r.Level).Should().Be(expected, $"{oldTool}: {relation}");
    }

    private sealed class FakeLocator(AbsolutePath? bwrap) : IExecutableLocator
    {
        public AbsolutePath? Find(string name) => name == "bwrap" ? bwrap : null;
    }

    private sealed class FakeProbe(string? failure) : ISandboxProbe
    {
        public string? Probe(AbsolutePath bwrap) => failure;
    }

    private static readonly AbsolutePath Bwrap = new InMemoryFileSystem().FromUnsanitizedFullPath("/usr/bin/bwrap");

    [Fact]
    public void OverlaySupportIsOkWhenBwrapMakesANamespace()
    {
        var result = EldenRingChecks.OverlaySupport(new FakeLocator(Bwrap), new FakeProbe(null));

        result.Id.Should().Be("overlay");
        result.Level.Should().Be(CheckLevel.Ok);
    }

    [Fact]
    public void OverlaySupportFailsWithoutBwrap()
    {
        var result = EldenRingChecks.OverlaySupport(new FakeLocator(null), new FakeProbe(null));

        result.Level.Should().Be(CheckLevel.Fail);
        result.Message.Should().Contain("bwrap was not found");
    }

    [Fact]
    public void OverlaySupportFailsAndSaysWhyWhenTheProbeFails()
    {
        var result = EldenRingChecks.OverlaySupport(new FakeLocator(Bwrap), new FakeProbe("it exited with code 1: no permission"));

        result.Level.Should().Be(CheckLevel.Fail);
        result.Message.Should().Contain("no permission");
    }

    [Fact]
    public void Me3WithAVersionIsOk()
    {
        var result = EldenRingChecks.Me3(Bwrap, executable: true, version: "me3 0.13.0");

        result.Id.Should().Be("me3");
        result.Level.Should().Be(CheckLevel.Ok);
        result.Message.Should().Contain("me3 0.13.0");
    }

    [Fact]
    public void Me3ThatIsNotExecutableWarns()
    {
        var result = EldenRingChecks.Me3(Bwrap, executable: false, version: null);

        result.Level.Should().Be(CheckLevel.Warn);
        result.Message.Should().Contain("/usr/bin/bwrap");
    }

    [Fact]
    public void Me3ThatReportsNoVersionWarns()
    {
        EldenRingChecks.Me3(Bwrap, executable: true, version: null).Level.Should().Be(CheckLevel.Warn);
    }

    [Fact]
    public void NothingPendingInTheUpperFolderGivesNoResult()
    {
        EldenRingChecks.Upperdir([]).Should().BeNull();
    }

    [Fact]
    public void PendingFindingsWarnAndListEachPath()
    {
        var result = EldenRingChecks.Upperdir([new AuditFinding(AuditFindingKind.Unexpected, "stray.txt"), new AuditFinding(AuditFindingKind.GameFolderChanged, "leaked.dll", "added")]);

        result!.Id.Should().Be("upperdir");
        result.Level.Should().Be(CheckLevel.Warn);
        result.Message.Should().Contain("stray.txt").And.Contain("leaked.dll");
    }
}
