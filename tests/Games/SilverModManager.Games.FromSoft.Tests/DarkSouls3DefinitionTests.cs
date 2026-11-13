using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.DarkSouls3;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class DarkSouls3DefinitionTests : IDisposable
{
    private static FromSoftGameDefinition Definition => DarkSouls3Game.Definition;

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-ds3-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void DarkSouls3PlaysOnlineAndHasNoLauncher()
    {
        Definition.HasOnlinePlay.Should().BeTrue();
        Definition.Layout.Launcher.Should().BeNull();
        Definition.Layout.GameSubfolder.Should().Be("Game");
    }

    [Fact]
    public void LoaderFilesWithoutALauncherFailBecauseTheGamePlaysOnline()
    {
        var root = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/DARK SOULS III/Game");
        root.CreateDirectory();
        foreach (var name in new[] { "DarkSoulsIII.exe", "dinput8.dll", "modengine2_launcher.exe" })
        {
            using var stream = (root / name).Create();
            stream.Write(new byte[1]);
        }

        var results = FromSoftChecks.GameFolder(root, Definition);

        var artifacts = results.Should().ContainSingle(r => r.Id == "artifacts").Subject;
        artifacts.Level.Should().Be(CheckLevel.Fail);
        artifacts.Message.Should().Contain("no anti-cheat gate").And.Contain("modengine2_launcher.exe");
    }

    [Fact]
    public void ModEngine2AndModEngine1LeftoversAreCleanupSteps()
    {
        string[] vanilla = ["DarkSoulsIII.exe", "Data0.bdt", "sound/x.fsb"];
        List<InventoryEntry> inventory = [..vanilla.Select(p => new InventoryEntry(p, InventoryKind.File, 10)), new("sound", InventoryKind.Directory, 0)];
        inventory.AddRange([
            new("modengine2_launcher.exe", InventoryKind.File, 10),
            new("config_darksouls3.toml", InventoryKind.File, 10),
            new("launchmod_darksouls3.bat", InventoryKind.File, 10),
            new("modengine.ini", InventoryKind.File, 10),
            new("mod", InventoryKind.Directory, 0),
            new("mod/parts/x.dcx", InventoryKind.File, 10),
        ]);
        var input = new CleanupInput("/steam/DARK SOULS III/Game", inventory.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray(),
            vanilla.ToHashSet(StringComparer.OrdinalIgnoreCase), false, null, false, null, "/data/cleanup/set-aside/1");

        var plan = CleanupPlanner.Build(input, Definition.Cleanup, Definition);

        plan.Steps.Where(s => s.Kind == CleanupStepKind.Delete).Select(s => s.Source)
            .Should().BeEquivalentTo("modengine2_launcher.exe", "config_darksouls3.toml", "launchmod_darksouls3.bat", "modengine.ini");
        plan.Steps.Should().ContainSingle(s => s.Kind == CleanupStepKind.SetAside).Which.Source.Should().Be("mod");
    }

    [Fact]
    public void TheLooseParamsRuleNamesMe3sThreeFilesAndTheRegulation()
    {
        Definition.LooseParams.Should().NotBeNull();
        Definition.LooseParams!.Files.Should().Equal(
            "param/gameparam/gameparam.parambnd.dcx", "param/gameparam/gameparam_dlc1.parambnd.dcx", "param/gameparam/gameparam_dlc2.parambnd.dcx");
        Definition.LooseParams.DisablesFile.Should().Be("Data0.bdt");
    }

    [Fact]
    public void TheModEngine1SaveIsListedAndAdopted()
    {
        var scanner = new SaveFolderScanner(Definition.SaveNames);
        scanner.KindOf("DS30000.sl3", out _).Should().Be(SaveKind.LegacyModded);

        var root = FileSystem.Shared.FromUnsanitizedFullPath(_root);
        File.WriteAllText(Path.Combine(_root, "DS30000.sl3"), "other loader");

        var plan = Definition.SaveNames.PlanAdoption("DS30000_abc.sl2", root);

        plan.Should().NotBeNull();
        plan!.Copies.Should().Equal(new SaveAdoptionPlan.Copy(root / "DS30000.sl3", root / "DS30000_abc.sl2"));
    }

    [Fact]
    public void TheRegulationIsLeftUnreadAndNoMergeIsOffered()
    {
        Definition.Formats.RegulationFileName.Should().BeNull();
        MergeStrategies.ForPath("Data0.bdt", Definition.Formats, true).Should().BeNull();
        MergeStrategies.ForPath("param/gameparam/gameparam.parambnd.dcx", Definition.Formats, true).Should().BeNull();
    }

    [Fact]
    public void TheDataArchiveIsSkippedByNameWhileTheRegulationIsUnread()
    {
        FromSoftPaths.IsArchiveOrMedia(Definition, new GamePath(LocationId.Game, "Game/Data0.bdt")).Should().BeTrue();
    }
}
