using System.Collections.Immutable;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

/// <summary>How the save step tells which mods a cleanup deletes: other managers' records, the lock file, archives, then a checked name.</summary>
public class SavePlannerIdentificationTests : IDisposable
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(4333);

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-ident-").FullName;
    private readonly CapturingLogger _log = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FakeCatalog Catalog { get; } = new();
    private FakeLibrary Library { get; } = new();

    private string GameFolder => Path.Combine(_root, "Game");
    private string Home => Path.Combine(_root, "home");

    private const string Instance = "home/.var/app/com.fluorine.manager/data/instances/Elden";

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private SaveScope Scope(params string[] folders)
    {
        Directory.CreateDirectory(GameFolder);
        var inventory = folders.Select(f => new InventoryEntry($"mods/{f}", InventoryKind.Directory, 0)).ToArray();
        var plan = new CleanupPlan([..folders.Select(f => new CleanupStep(CleanupStepKind.Delete, $"Delete {f}", $"mods/{f}"))]);
        return new SaveScope(GameFolder, "mods", inventory, plan, new ManagerSearch(Home, [], []), ["Elden Ring"]);
    }

    private Task<SavePlan> Plan(params string[] folders) => new SavePlanner(Catalog, Library, _log).PlanAsync(Game, Scope(folders), default);

    private void FluorineKnows(string mod, string modId, string? fileId, string version = "1.0", string? archive = null)
    {
        var gamePath = Tree(GameFolder);
        WriteFile($"{Instance}/ModOrganizer.ini", $"[General]\ngameName=Elden Ring\ngamePath=@ByteArray({gamePath})\n");
        WriteFile($"{Instance}/mods/{mod}/meta.ini",
            $"[General]\nmodid={modId}\nversion={version}\ninstallationFile={archive}\n[installedFiles]\n"
            + (fileId is null ? "size=0\n" : $"1\\modid={modId}\n1\\fileid={fileId}\nsize=1\n"));
    }

    private static string Tree(string unixPath) => ("Z:" + unixPath.Replace('/', '\\')).Replace("\\", "\\\\");

    private const string LockFor = """
        [noweight]
        version = "1.0"
        file_id = "99"
        asset = "NoWeight-77-1-0-1700000000.zip"
        source = "nexus"
        """;

    [Fact]
    public async Task AFluorineInstanceIdentifiesAFolderWithoutAskingNexusMods()
    {
        FluorineKnows("NoWeight", "77", "5000", "2.0");

        var plan = await Plan("NoWeight");

        plan.Save.Should().Equal(new ModToSave("NoWeight", "NoWeight", "2.0", 77, 5000, "from Fluorine's records"));
        Catalog.Calls.Should().NotContain(c => c.StartsWith("search"));
    }

    [Fact]
    public async Task FluorinesRecordsBeatTheLockFileAndTheDisagreementIsLogged()
    {
        FluorineKnows("NoWeight", "77", "5000");
        WriteFile("Game/mods.lock.toml", LockFor);

        var plan = await Plan("NoWeight");

        plan.Save.Should().ContainSingle().Which.Should().Match<ModToSave>(m => m.FileId == 5000 && m.IdentifiedBy == "from Fluorine's records");
        _log.Warnings.Should().ContainSingle().Which.Should().Contain("Fluorine").And.Contain("erm").And.Contain("99");
    }

    [Fact]
    public async Task AModNobodyElseKnowsIsIdentifiedFromTheLockFile()
    {
        WriteFile("Game/mods.lock.toml", LockFor);

        var plan = await Plan("NoWeight");

        plan.Save.Should().ContainSingle().Which.Should().Match<ModToSave>(m => m.FileId == 99 && m.IdentifiedBy == "from erm's lock file");
    }

    [Fact]
    public async Task AModWithOnlyAModIdInTheRecordsIsResolvedByItsVersion()
    {
        FluorineKnows("NoWeight", "77", null, "2.0");
        Catalog.Files[77] = [new NexusFile(1, "NoWeight old", "1.0", 1), new NexusFile(2, "NoWeight", "2.0", 2)];

        var plan = await Plan("NoWeight");

        plan.Save.Should().ContainSingle().Which.FileId.Should().Be(2);
    }

    [Fact]
    public async Task AnArchiveInDownloadsIdentifiesAModByItsHash()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        Directory.CreateDirectory(Path.Combine(Home, "Downloads"));
        File.WriteAllBytes(Path.Combine(Home, "Downloads", "SkipTheIntro-421-4-0-1732388874.zip"), bytes);
        Catalog.Archives[md5] = new ArchiveHit(md5, "SkipTheIntro-421-4-0-1732388874.zip", Game.Value, 421, 32781);

        var plan = await Plan("SkipTheIntro");

        plan.Save.Should().ContainSingle().Which.Should().Match<ModToSave>(m => m.ModId == 421 && m.FileId == 32781 && m.IdentifiedBy == "matched its archive");
    }

    [Fact]
    public async Task ALockEntryWithoutAFileIdTakesItFromItsArchiveInsteadOfGuessing()
    {
        WriteFile("Game/mods.lock.toml", """
            [skiptheintro]
            version = "4.0"
            asset = "SkipTheIntro-421-4-0-1732388874.zip"
            source = "nexus"
            """);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        Directory.CreateDirectory(Path.Combine(Home, "Downloads"));
        File.WriteAllBytes(Path.Combine(Home, "Downloads", "SkipTheIntro-421-4-0-1732388874.zip"), bytes);
        Catalog.Archives[md5] = new ArchiveHit(md5, "SkipTheIntro-421-4-0-1732388874.zip", Game.Value, 421, 32781);
        Catalog.ModOfFile[32781] = 421;

        var plan = await Plan("SkipTheIntro");

        plan.Save.Should().ContainSingle().Which.Should().Match<ModToSave>(m => m.ModId == 421 && m.FileId == 32781 && m.IdentifiedBy == "from erm's lock file");
    }

    [Fact]
    public async Task LooseFilesInTheGameFolderAreNeverHashed()
    {
        WriteFile("Game/mods/Loose/Loose.dll", "abc");
        WriteFile("Game/Loose.zip", "inside the game folder");
        Directory.CreateDirectory(Path.Combine(Home, "Downloads"));
        File.WriteAllBytes(Path.Combine(Home, "Downloads", "a.zip"), [9, 9]);

        await Plan("Loose");

        Catalog.HashLookups.Should().ContainSingle().Which.Should().Equal(Convert.ToHexStringLower(MD5.HashData(new byte[] { 9, 9 })));
    }

    [Fact]
    public async Task AnArchiveBesideTheLocksVendorFolderIsHashed()
    {
        WriteFile("Game/mods.lock.toml", "# empty\n");
        var bytes = new byte[] { 3, 3 };
        WriteFile("Game/vendor/placeholder.txt", "x");
        File.WriteAllBytes(Path.Combine(GameFolder, "vendor", "v.zip"), bytes);

        await Plan("Whatever");

        Catalog.HashLookups.Should().ContainSingle();
    }

    [Fact]
    public async Task AThunderstorePackageIsListedNotDownloaded()
    {
        WriteFile("home/.config/r2modmanPlus-local/Elden Ring/profiles/Default/mods.yml", "- name: Owner-CoolMod\n  versionNumber: 1.2.3\n");

        var plan = await Plan("CoolMod");

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle().Which.Should().Match<ListedMod>(l => l.Reason == ListedReason.Thunderstore && l.Detail.Contains("Owner-CoolMod") && l.Detail.Contains("1.2.3"));
    }

    [Fact]
    public async Task ARecordOfAnotherGameIsNotUsedAndTheFolderFallsBackToItsName()
    {
        WriteFile($"{Instance}/ModOrganizer.ini", $"[General]\ngameName=Skyrim\ngamePath=@ByteArray({Tree(Path.Combine(_root, "Skyrim"))})\n");
        WriteFile($"{Instance}/mods/NoWeight/meta.ini", "[General]\nmodid=1\n[installedFiles]\n1\\fileid=2\n");

        var plan = await Plan("NoWeight");

        plan.Save.Should().BeEmpty();
        Catalog.Calls.Should().Contain("search NoWeight");
    }

    private void NameMatchesOneMod(string folder, string localContent, long listedSize)
    {
        Catalog.Search[folder] = [new NexusModHit(117, folder)];
        Catalog.MainFile[117] = 3000;
        Catalog.Files[117] = [new NexusFile(3000, folder, "3.2", 1_662_569_069)];
        WriteFile($"Game/mods/{folder}/{folder}.dll", localContent);
        Catalog.Content[(117, 3000)] = [new ArchiveEntry($"{folder}.dll", listedSize)];
    }

    [Fact]
    public async Task ANameMatchIsAcceptedOnlyWhenTheFolderHoldsTheArchivesFiles()
    {
        NameMatchesOneMod("NoWeight", "12345", 5);

        var plan = await Plan("NoWeight");

        plan.Save.Should().ContainSingle().Which.IdentifiedBy.Should().Be("by name, files checked");
    }

    [Fact]
    public async Task ASameNamedModWithDifferentFileSizesIsRejected()
    {
        NameMatchesOneMod("NoWeight", "12345", 4096);

        var plan = await Plan("NoWeight");

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle().Which.Should().Match<ListedMod>(l => l.Reason == ListedReason.Unidentified && l.Detail.Contains("do not match"));
    }

    [Fact]
    public async Task ANameMatchWithoutAFileListingCannotBeCheckedAndIsListed()
    {
        NameMatchesOneMod("NoWeight", "12345", 5);
        Catalog.Content.Clear();

        var plan = await Plan("NoWeight");

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle().Which.Detail.Should().Contain("listing");
    }

    [Fact]
    public async Task AFolderMissingFromDiskCannotBeCheckedAndIsListed()
    {
        Catalog.Search["Ghost"] = [new NexusModHit(5, "Ghost")];
        Catalog.MainFile[5] = 6;
        Catalog.Files[5] = [new NexusFile(6, "Ghost", "1", 1)];
        Catalog.Content[(5, 6)] = [new ArchiveEntry("g.dll", 1)];

        var plan = await Plan("Ghost");

        plan.Save.Should().BeEmpty();
        plan.Listed.Should().ContainSingle();
    }

    [Fact]
    public async Task ALockThatNamesAFolderMeansNoNameGuessIsMadeForOthers()
    {
        WriteFile("Game/mods.lock.toml", LockFor);

        await Plan("Some Folder");

        Catalog.Calls.Should().NotContain(c => c.StartsWith("search"));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
