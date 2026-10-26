using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

public class SavePlannerTests : IDisposable
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(4333);

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-save-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FakeCatalog Catalog { get; } = new();
    private FakeLibrary Library { get; } = new();

    private string GameFolder => Path.Combine(_root, "Game");

    private SavePlanner Planner => new(Catalog, Library);

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static CleanupPlan Plan(params string[] deletes)
        => new([..deletes.Select(d => new CleanupStep(CleanupStepKind.Delete, $"Delete {d}", d))]);

    private SaveScope Scope(string[] modFolders, params InventoryEntry[] extra)
    {
        Directory.CreateDirectory(GameFolder);
        var inventory = modFolders.Select(f => new InventoryEntry($"mods/{f}", InventoryKind.Directory, 0)).Concat(extra).ToArray();
        return new SaveScope(GameFolder, "mods", inventory, Plan(modFolders.Select(f => $"mods/{f}").ToArray()));
    }

    private const string TwoEntryLock = """
        [adjust-the-fov]
        version = "2.0"
        file_id = "5087"
        asset = "AdjustTheFov-325-2-0-1653830963.zip"
        sha256 = "00"
        source = "nexus"

        [me3-host]
        version = "v0.13.0"
        asset = "me3-host-v0.13.0.tar.gz"
        sha256 = "22"
        source = "github"
        """;

    [Fact]
    public async Task ALockInTheGameFolderListsItsNexusModsToSaveAndItsGitHubModsAsNotDownloaded()
    {
        WriteFile("Game/mods.lock.toml", TwoEntryLock);

        var plan = await Planner.PlanAsync(Game, Scope(["x"]), default);

        plan.Save.Should().Equal(new ModToSave("adjust-the-fov", "adjust-the-fov", "2.0", 325, 5087, "from erm's lock file"));
        var github = plan.Listed.Should().ContainSingle().Subject;
        github.Reason.Should().Be(ListedReason.GitHub);
        github.Id.Should().Be("me3-host");
        github.Detail.Should().Contain("GitHub");
    }

    [Fact]
    public async Task ALockBesideTheModsFolderIsFound()
    {
        WriteFile("Game/mods/mods.lock.toml", TwoEntryLock);

        var plan = await Planner.PlanAsync(Game, Scope(["x"]), default);

        plan.Save.Should().HaveCount(1);
    }

    [Fact]
    public async Task ALockAboveWhereALinkPointsIsFound()
    {
        WriteFile("erm/mods.lock.toml", TwoEntryLock);
        Directory.CreateDirectory(Path.Combine(_root, "erm/vendor/reshade/Shaders"));
        var link = new InventoryEntry("ReShade_shaders", InventoryKind.Link, 0, Path.Combine(_root, "erm/vendor/reshade/Shaders"));

        var plan = await Planner.PlanAsync(Game, Scope(["x"], link), default);

        plan.Save.Should().HaveCount(1);
    }

    [Fact]
    public async Task AFileInTheLockAlreadyInTheLibraryIsListedNotSaved()
    {
        WriteFile("Game/mods.lock.toml", TwoEntryLock);
        Library.Files.Add(5087);

        var plan = await Planner.PlanAsync(Game, Scope(["x"]), default);

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().Contain(l => l.Id == "adjust-the-fov" && l.Reason == ListedReason.AlreadyInLibrary);
    }

    [Fact]
    public async Task AnEntryThatCannotBeIdentifiedIsListedWithTheReason()
    {
        WriteFile("Game/mods.lock.toml", """
            [questpath]
            version = "1.3.0"
            asset = "QuestPath 10363 1.3.0 2026-07-19T20-46Z NhlU57Tcv.rar"
            source = "nexus"
            """);
        Catalog.Files[10363] =
        [
            new NexusFile(100, "A", "1.3.0", new DateTimeOffset(2026, 7, 19, 20, 46, 10, TimeSpan.Zero).ToUnixTimeSeconds()),
            new NexusFile(101, "B", "1.3.0", new DateTimeOffset(2026, 7, 19, 20, 46, 50, TimeSpan.Zero).ToUnixTimeSeconds()),
        ];

        var plan = await Planner.PlanAsync(Game, Scope(["x"]), default);

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle(l => l.Id == "questpath" && l.Reason == ListedReason.Unidentified);
    }

    [Fact]
    public async Task AGameWithoutANexusIdListsEverythingAsUnidentified()
    {
        WriteFile("Game/mods.lock.toml", TwoEntryLock);

        var plan = await Planner.PlanAsync(null, Scope(["x"]), default);

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().Contain(l => l.Id == "adjust-the-fov" && l.Reason == ListedReason.Unidentified && l.Detail.Contains("Nexus Mods"));
    }

    [Fact]
    public async Task WithoutALockAFolderWhoseNameIsExactlyOneModsNameIsIdentified()
    {
        Catalog.Search["Elden Mod Loader"] = [new NexusModHit(117, "Elden Mod Loader")];
        Catalog.MainFile[117] = 3000;
        Catalog.Files[117] = [new NexusFile(3000, "Elden Mod Loader", "3.2", 1_662_569_069)];
        WriteFile("Game/mods/Elden Mod Loader/loader.dll", "12345");
        Catalog.Content[(117, 3000)] = [new ArchiveEntry("loader.dll", 5)];

        var plan = await Planner.PlanAsync(Game, Scope(["Elden Mod Loader"]), default);

        plan.Save.Should().Equal(new ModToSave("Elden Mod Loader", "Elden Mod Loader", "3.2", 117, 3000, "by name, files checked"));
    }

    [Fact]
    public async Task AFolderThatMatchesSeveralModsOrNoneIsListedAsUnidentified()
    {
        Catalog.Search["Randomizer"] = [new NexusModHit(1, "Randomizer"), new NexusModHit(2, "Randomizer")];
        Catalog.Search["Faster"] = [new NexusModHit(3, "Faster Respawn")];

        var plan = await Planner.PlanAsync(Game, Scope(["Randomizer", "Faster", "Unknown"]), default);

        plan.Save.Should().BeEmpty();
        plan.Listed.Select(l => (l.Id, l.Reason)).Should().BeEquivalentTo(
        [
            ("Randomizer", ListedReason.Unidentified),
            ("Faster", ListedReason.Unidentified),
            ("Unknown", ListedReason.Unidentified),
        ]);
    }

    [Fact]
    public async Task AFolderMatchWithoutASingleMainFileIsListedAsUnidentified()
    {
        Catalog.Search["Two Files"] = [new NexusModHit(5, "Two Files")];

        var plan = await Planner.PlanAsync(Game, Scope(["Two Files"]), default);

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle(l => l.Reason == ListedReason.Unidentified);
    }

    [Fact]
    public async Task ALockThatIsFoundMeansNoFolderIsSearchedFor()
    {
        WriteFile("Game/mods.lock.toml", TwoEntryLock);

        await Planner.PlanAsync(Game, Scope(["Some Folder"]), default);

        Catalog.Calls.Should().NotContain(c => c.StartsWith("search"));
    }

    [Fact]
    public async Task ALockWithNothingUsableFallsBackToTheFolders()
    {
        WriteFile("Game/mods.lock.toml", "not toml at all [[[");
        Catalog.Search["Elden Mod Loader"] = [new NexusModHit(117, "elden mod loader")];
        Catalog.MainFile[117] = 3000;
        Catalog.Files[117] = [new NexusFile(3000, "Elden Mod Loader", "3.2", 1_662_569_069)];
        WriteFile("Game/mods/Elden Mod Loader/loader.dll", "12345");
        Catalog.Content[(117, 3000)] = [new ArchiveEntry("loader.dll", 5)];

        var plan = await Planner.PlanAsync(Game, Scope(["Elden Mod Loader"]), default);

        plan.Save.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyFoldersTheCleanupDeletesAreSearchedFor()
    {
        var scope = Scope(["Doomed"]) with { Plan = Plan("mods/Doomed", "other/File") };

        await Planner.PlanAsync(Game, scope, default);

        Catalog.Calls.Should().Equal("search Doomed");
    }

    [Fact]
    public async Task NothingToDeleteAndNoLockGivesAnEmptyPlan()
    {
        var plan = await Planner.PlanAsync(Game, new SaveScope(GameFolder, "mods", [], Plan()), default);

        plan.IsEmpty.Should().BeTrue();
        Catalog.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOversizedLockIsReportedNotRead()
    {
        WriteFile("Game/mods.lock.toml", new string('#', ProvenanceReader.MaxChars + 10));

        var plan = await Planner.PlanAsync(Game, Scope(["x"]), default);

        plan.Save.Should().BeEmpty();
        plan.Problems.Should().ContainSingle().Which.Should().Contain("too large");
    }
}
