using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Tests.Fixtures;

namespace NexusMods.Games.FromSoft.Tests.Cleanup;

public class CleanupPlannerTests
{
    private const string Folder = "/steam/ELDEN RING/Game";

    private static readonly string[] Vanilla =
    [
        "eldenring.exe", "start_protected_game.exe", "regulation.bin", "Data0.bdt", "sd/sd.bdt", "movie/intro.bk2", "oo2core_6_win64.dll",
    ];

    private static InventoryEntry File(string path, long size = 10) => new(path, InventoryKind.File, size);
    private static InventoryEntry Dir(string path) => new(path, InventoryKind.Directory, 0);
    private static InventoryEntry Link(string path, string target) => new(path, InventoryKind.Link, 0, target);

    private static List<InventoryEntry> VanillaInventory()
    {
        var entries = Vanilla.Select(p => File(p)).ToList();
        entries.Add(Dir("sd"));
        entries.Add(Dir("movie"));
        return entries;
    }

    private static CleanupInput Input(IEnumerable<InventoryEntry> inventory, bool immutable = false, LaunchOptionChange? launchOption = null, bool updatePending = false, bool baseline = true)
        => new(Folder, inventory.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray(), baseline ? Vanilla.ToHashSet(StringComparer.OrdinalIgnoreCase) : null, immutable, launchOption, updatePending);

    private static CleanupPlan Build(CleanupInput input)
        => CleanupPlanner.Build(input, EldenRingGame.Definition.Cleanup, EldenRingGame.Definition.Layout);

    private static List<InventoryEntry> ErmInstall()
    {
        var entries = VanillaInventory();
        entries.Add(File("start_protected_game.exe.erm-backup"));
        entries.Add(Dir("mods"));
        entries.Add(Dir("mods/AdjustTheFov"));
        entries.Add(File("mods/AdjustTheFov/config.ini"));
        entries.Add(File("mods/AdjustTheFov/adjust.dll"));
        entries.Add(Link("dxgi.dll", "/reshade/ReShade64.dll"));
        entries.Add(Link("ReShade_shaders", "/reshade/Shaders"));
        entries.Add(Dir("SeamlessCoop"));
        entries.Add(Dir("SeamlessCoop/crashdumps"));
        entries.Add(File("SeamlessCoop/crashdumps/x.dmp"));
        entries.Add(File("ReShade.log"));
        entries.Add(File("vkd3d-proton.cache"));
        return entries;
    }

    [Fact]
    public void APristineFolderHasNoSteps()
    {
        var plan = Build(Input(VanillaInventory().Append(File("vkd3d-proton.cache"))));

        plan.IsEmpty.Should().BeTrue();
        plan.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void AnInstallLeftByAnotherModManagerGetsItsStepsInOrder()
    {
        var plan = Build(Input(ErmInstall()));

        plan.Steps.Select(s => (s.Kind, s.Source, s.Target)).Should().Equal(
            (CleanupStepKind.Move, "start_protected_game.exe.erm-backup", "start_protected_game.exe"),
            (CleanupStepKind.Copy, "mods/AdjustTheFov/config.ini", "overlay/mods/AdjustTheFov/config.ini"),
            (CleanupStepKind.Delete, "mods/AdjustTheFov", null),
            (CleanupStepKind.Delete, "mods", null),
            (CleanupStepKind.Delete, "ReShade_shaders", null),
            (CleanupStepKind.Delete, "dxgi.dll", null),
            (CleanupStepKind.Delete, "ReShade.log", null),
            (CleanupStepKind.Delete, "SeamlessCoop", null));
        plan.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void ABackupWithoutTheLauncherBesideItIsStillRestored()
    {
        var inventory = VanillaInventory().Where(e => e.Path != "start_protected_game.exe").Append(File("start_protected_game.exe.erm-backup"));

        var plan = Build(Input(inventory));

        plan.Steps.Select(s => (s.Kind, s.Source, s.Target)).Should().Equal(
            (CleanupStepKind.Move, "start_protected_game.exe.erm-backup", "start_protected_game.exe"));
        plan.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void AnImmutableLauncherComesFirstAsACommandForTheUserAndBlocksTheRestore()
    {
        var plan = Build(Input(ErmInstall(), immutable: true));

        plan.Steps[0].Kind.Should().Be(CleanupStepKind.UserCommand);
        plan.Steps[0].Description.Should().Contain($"sudo chattr -i '{Folder}/start_protected_game.exe'");
        plan.Steps[1].Kind.Should().Be(CleanupStepKind.Move);
        plan.Steps[1].IsBlocked.Should().BeTrue();
        plan.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public void ThePathInTheCommandIsQuotedAgainstQuotes()
    {
        var input = Input(ErmInstall(), immutable: true) with { GameFolder = "/steam/it's here/Game" };

        Build(input).Steps[0].Description.Should().Contain(@"sudo chattr -i '/steam/it'\''s here/Game/start_protected_game.exe'");
    }

    [Fact]
    public void EveryIniOfAModIsCopiedAtTheSameRelativePathBeforeTheFolderGoes()
    {
        var inventory = VanillaInventory();
        inventory.AddRange([Dir("mods"), Dir("mods/Fov"), File("mods/Fov/a.ini"), Dir("mods/Fov/sub"), File("mods/Fov/sub/B.INI"), File("mods/Fov/readme.txt")]);

        var plan = Build(Input(inventory));

        plan.Steps.Select(s => (s.Kind, s.Source, s.Target)).Should().Equal(
            (CleanupStepKind.Copy, "mods/Fov/a.ini", "overlay/mods/Fov/a.ini"),
            (CleanupStepKind.Copy, "mods/Fov/sub/B.INI", "overlay/mods/Fov/sub/B.INI"),
            (CleanupStepKind.Delete, "mods/Fov", null),
            (CleanupStepKind.Delete, "mods", null));
    }

    [Fact]
    public void AStrayFileInTheModsFolderKeepsTheFolderAndIsReviewed()
    {
        var inventory = VanillaInventory();
        inventory.AddRange([Dir("mods"), Dir("mods/Fov"), File("mods/Fov/a.ini"), File("mods/notes.txt")]);

        var plan = Build(Input(inventory));

        plan.Steps.Should().NotContain(s => s.Source == "mods");
        plan.Steps.Should().ContainSingle(s => s.Kind == CleanupStepKind.Review && s.Source == "mods/notes.txt");
    }

    [Fact]
    public void ALinkIsDeletedAsALinkAndNeverFollowed()
    {
        var plan = Build(Input(VanillaInventory().Append(Link("d3dcompiler_47.dll", "/reshade/d3dcompiler_47.dll"))));

        var step = plan.Steps.Should().ContainSingle().Subject;
        step.Kind.Should().Be(CleanupStepKind.Delete);
        step.Source.Should().Be("d3dcompiler_47.dll");
        step.Description.Should().Contain("link").And.Contain("/reshade/d3dcompiler_47.dll");
    }

    [Fact]
    public void LeftoversOfTheOldLoaderAreDeletedWhateverTheirCase()
    {
        var inventory = VanillaInventory();
        inventory.AddRange(new[] { "ReShade2.ini", "ersc_launcher.exe", "DINPUT8.dll", "winhttp.dll", "modengine2.dll", "modengine.toml" }.Select(p => File(p)));
        inventory.AddRange([Dir("mod"), File("mod/regulation.bin"), File("crash.dmp")]);

        var plan = Build(Input(inventory));

        plan.Steps.Should().OnlyContain(s => s.Kind == CleanupStepKind.Delete);
        plan.Steps.Select(s => s.Source).Should().BeEquivalentTo("ReShade2.ini", "ersc_launcher.exe", "DINPUT8.dll", "winhttp.dll", "modengine2.dll", "modengine.toml", "mod", "crash.dmp");
    }

    [Fact]
    public void ADumpDeepInsideAFolderOfTheGameIsDeleted()
    {
        var plan = Build(Input(VanillaInventory().Append(File("sd/crash.dmp"))));

        plan.Steps.Should().ContainSingle(s => s.Kind == CleanupStepKind.Delete && s.Source == "sd/crash.dmp");
    }

    [Fact]
    public void AFileTheGameDoesNotShipIsListedForReviewAndNotActedOn()
    {
        var plan = Build(Input(VanillaInventory().Append(File("mystery.bin"))));

        var step = plan.Steps.Should().ContainSingle().Subject;
        step.Kind.Should().Be(CleanupStepKind.Review);
        step.Source.Should().Be("mystery.bin");
        step.Acts.Should().BeFalse();
    }

    [Fact]
    public void ALauncherMissingFromTheFileListIsNotReviewed()
    {
        // The list is built from the disk, so a launcher that was swapped at the time is not on it.
        var input = Input(VanillaInventory().Append(File("start_protected_game.exe.erm-backup")), baseline: true);
        input = input with { Baseline = input.Baseline!.Where(p => p != "start_protected_game.exe").ToHashSet(StringComparer.OrdinalIgnoreCase) };

        var plan = Build(input);

        plan.Steps.Select(s => s.Kind).Should().Equal(CleanupStepKind.Move);
    }

    [Fact]
    public void TheGamesArchivesAreNotReviewedWhateverTheFileListSays()
    {
        var input = Input(VanillaInventory().Append(File("Data1.bdt")).Append(File("sd/sd_en.bdt")).Append(File("movie/outro.bk2")), baseline: true);

        Build(input).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void WithoutAFileListForTheBuildNothingIsCalledUnrecognisedAndTheUserIsTold()
    {
        var plan = Build(Input(VanillaInventory().Append(File("mystery.bin")), baseline: false));

        plan.Steps.Should().NotContain(s => s.Kind == CleanupStepKind.Review);
        plan.Steps.Should().ContainSingle(s => s.Kind == CleanupStepKind.Advice);
    }

    [Fact]
    public void AnotherManagersLaunchOptionsAreReplacedAfterTheFileSteps()
    {
        var change = new LaunchOptionChange("me3 launch -- %command%", "'/shim' 'Main' %command%");

        var plan = Build(Input(ErmInstall(), launchOption: change));

        var last = plan.Steps[^1];
        last.Kind.Should().Be(CleanupStepKind.ReplaceLaunchOption);
        last.Source.Should().Be(change.OldLine);
        last.Target.Should().Be(change.NewLine);
        last.Description.Should().Contain(change.OldLine).And.Contain(change.NewLine);
    }

    [Fact]
    public void APendingSteamUpdateIsNoReasonToRefuseAndEndsThePlanWithAnAdvice()
    {
        var plan = Build(Input(ErmInstall(), updatePending: true));

        plan.Steps[^1].Kind.Should().Be(CleanupStepKind.Advice);
        plan.Steps[^1].Description.Should().Contain("Steam").And.Contain("update");
        plan.IsBlocked.Should().BeFalse();
    }

    [Fact]
    public void APristineFolderWithAPendingUpdateOnlyAdvises()
    {
        var plan = Build(Input(VanillaInventory(), updatePending: true));

        plan.Steps.Select(s => s.Kind).Should().Equal(CleanupStepKind.Advice);
    }

    [Fact]
    public void TwoPlansOfTheSameFolderHaveTheSameSteps()
    {
        Build(Input(ErmInstall())).SameSteps(Build(Input(ErmInstall()))).Should().BeTrue();
        Build(Input(ErmInstall())).SameSteps(Build(Input(VanillaInventory()))).Should().BeFalse();
        new CleanupPlan(ImmutableArray<CleanupStep>.Empty).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AGameWithoutEldenRingsRulesLeavesEldenRingsNamesAlone()
    {
        var inventory = VanillaInventory().Concat([Dir("SeamlessCoop"), File("ersc_launcher.exe"), File("start_protected_game.exe.erm-backup")]);

        var plan = CleanupPlanner.Build(Input(inventory), CleanupRules.ModEngine2, TestSoulsDefinition.Layout);

        plan.Steps.Select(s => s.Kind).Should().NotContain([CleanupStepKind.Move, CleanupStepKind.Delete]);
        plan.Steps.Where(s => s.Kind == CleanupStepKind.Review).Select(s => s.Source)
            .Should().BeEquivalentTo(["ersc_launcher.exe", "start_protected_game.exe.erm-backup"]);
    }

    [Fact]
    public void AGameWithoutAModsFolderConventionKeepsAModsFolderForReview()
    {
        var inventory = VanillaInventory().Concat([Dir("mods"), Dir("mods/Foo"), File("mods/Foo/config.ini")]);

        var plan = CleanupPlanner.Build(Input(inventory), CleanupRules.ModEngine2, TestSoulsDefinition.Layout);

        plan.Steps.Select(s => s.Kind).Should().NotContain([CleanupStepKind.Copy, CleanupStepKind.Delete]);
    }
}
