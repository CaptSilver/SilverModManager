using FluentAssertions;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Games.Generic.ModProvenance;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

public class NexusFileResolverTests
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(4333);

    private FakeCatalog Catalog { get; } = new();

    private NexusFileResolver Resolver => new(Catalog);

    private static long Unix(int year, int month, int day, int hour, int minute, int second = 0)
        => new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds();

    private static ProvenanceEntry Entry(string asset, string version, uint? fileId = null)
        => new("some-mod", ProvenanceSources.Nexus, version, fileId, asset);

    [Fact]
    public async Task AFileIdInTheLockNeedsNoLookupWhenTheArchiveNameGivesTheMod()
    {
        var result = await Resolver.ResolveAsync(Game, Entry("AdjustTheFov-325-2-0-1653830963.zip", "2.0", fileId: 5087), default);

        result.Why.Should().BeNull();
        result.Mod.Should().Be(new ModToSave("some-mod", "some-mod", "2.0", 325, 5087));
        Catalog.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileIdWithoutAnArchiveNameAsksNexusWhichModOwnsIt()
    {
        Catalog.ModOfFile[47405] = 10079;

        var result = await Resolver.ResolveAsync(Game, new ProvenanceEntry("melina", ProvenanceSources.Nexus, "1.1", 47405, null), default);

        result.Mod.Should().Be(new ModToSave("melina", "melina", "1.1", 10079, 47405));
    }

    [Fact]
    public async Task AFileIdNobodyOwnsIsNotIdentified()
    {
        var result = await Resolver.ResolveAsync(Game, new ProvenanceEntry("melina", ProvenanceSources.Nexus, "1.1", 47405, null), default);

        result.Mod.Should().BeNull();
        result.Why.Should().Contain("47405");
    }

    [Fact]
    public async Task WithoutAFileIdTheFileWithTheArchivesNameWins()
    {
        Catalog.Files[325] =
        [
            new NexusFile(5087, "Adjust The FOV", "2.0", Unix(2022, 5, 29, 13, 29, 23)),
            new NexusFile(5088, "Adjust The FOV - extras", "2.0", Unix(2022, 5, 29, 13, 29, 23)),
        ];

        var result = await Resolver.ResolveAsync(Game, Entry("AdjustTheFov-325-2-0-1653830963.zip", "2.0"), default);

        result.Mod!.FileId.Should().Be(5087u);
        result.Mod.Name.Should().Be("Adjust The FOV");
    }

    [Fact]
    public async Task WithoutANameMatchTheFileWithTheVersionWins()
    {
        Catalog.Files[7] =
        [
            new NexusFile(1, "Old", "1.0", 1_600_000_000),
            new NexusFile(2, "Current", "1.2", 1_700_000_000),
        ];

        var result = await Resolver.ResolveAsync(Game, Entry("Renamed-7-1-2-1700000000.zip", "1.2"), default);

        result.Mod!.FileId.Should().Be(2u);
    }

    [Fact]
    public async Task TwoFilesWithTheSameVersionAreToldApartByUploadMinute()
    {
        // The main file and a language pack, uploaded a minute apart.
        Catalog.Files[10363] =
        [
            new NexusFile(100, "QuestPath main", "1.3.0", Unix(2026, 7, 19, 20, 46, 10)),
            new NexusFile(101, "QuestPath language pack", "1.3.0", Unix(2026, 7, 19, 20, 47, 5)),
        ];

        var result = await Resolver.ResolveAsync(Game, Entry("QuestPath 10363 1.3.0 2026-07-19T20-46Z NhlU57Tcv.rar", "1.3.0"), default);

        result.Why.Should().BeNull();
        result.Mod!.FileId.Should().Be(100u);
    }

    [Fact]
    public async Task AnOldStyleNameIsComparedToTheSecond()
    {
        Catalog.Files[9] =
        [
            new NexusFile(1, "A", "1.0", 1_700_000_000),
            new NexusFile(2, "B", "1.0", 1_700_000_030),
        ];

        var result = await Resolver.ResolveAsync(Game, Entry("Thing-9-1-0-1700000030.zip", "1.0"), default);

        result.Mod!.FileId.Should().Be(2u);
    }

    [Fact]
    public async Task FilesThatStayIndistinguishableAreNotGuessedBetween()
    {
        Catalog.Files[10363] =
        [
            new NexusFile(100, "QuestPath main", "1.3.0", Unix(2026, 7, 19, 20, 46, 10)),
            new NexusFile(101, "QuestPath language pack", "1.3.0", Unix(2026, 7, 19, 20, 46, 50)),
        ];

        var result = await Resolver.ResolveAsync(Game, Entry("QuestPath 10363 1.3.0 2026-07-19T20-46Z NhlU57Tcv.rar", "1.3.0"), default);

        result.Mod.Should().BeNull();
        result.Why.Should().Contain("more than one");
    }

    [Fact]
    public async Task NoFileOfTheRightVersionIsNotIdentified()
    {
        Catalog.Files[7] = [new NexusFile(1, "Old", "1.0", 1_600_000_000)];

        var result = await Resolver.ResolveAsync(Game, Entry("Renamed-7-1-2-1700000000.zip", "1.2"), default);

        result.Mod.Should().BeNull();
        result.Why.Should().Contain("1.2");
    }

    [Fact]
    public async Task AModNexusDoesNotKnowIsNotIdentified()
    {
        var result = await Resolver.ResolveAsync(Game, Entry("Gone-5-1-0-1700000000.zip", "1.0"), default);

        result.Mod.Should().BeNull();
        result.Why.Should().Contain("5");
    }

    [Fact]
    public async Task AnEntryWithNeitherFileIdNorReadableArchiveNameIsNotIdentified()
    {
        var result = await Resolver.ResolveAsync(Game, Entry("erd-tools-1.4.0.zip", "1.4.0"), default);

        result.Mod.Should().BeNull();
        result.Why.Should().NotBeNullOrEmpty();
        Catalog.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailingLookupIsReportedPerEntryInsteadOfThrown()
    {
        var catalog = new ThrowingCatalog();

        var result = await new NexusFileResolver(catalog).ResolveAsync(Game, Entry("Gone-5-1-0-1700000000.zip", "1.0"), default);

        result.Mod.Should().BeNull();
        result.Why.Should().Contain("offline");
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var catalog = new CancellingCatalog();

        var act = () => new NexusFileResolver(catalog).ResolveAsync(Game, Entry("Gone-5-1-0-1700000000.zip", "1.0"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class ThrowingCatalog : INexusCatalog
    {
        public Task<IReadOnlyList<NexusFile>?> ListFilesAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task<uint?> ModOfFileAsync(NexusModsGameId game, uint fileId, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task<IReadOnlyList<NexusModHit>> FindModsByNameAsync(NexusModsGameId game, string name, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task<uint?> FindMainFileAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task<IReadOnlyList<ArchiveHit>> LookupArchivesAsync(IReadOnlyList<string> md5s, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
        public Task<IReadOnlyList<ArchiveEntry>?> ListArchiveContentAsync(uint game, uint modId, uint fileId, CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }

    private sealed class CancellingCatalog : INexusCatalog
    {
        public Task<IReadOnlyList<NexusFile>?> ListFilesAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public Task<uint?> ModOfFileAsync(NexusModsGameId game, uint fileId, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public Task<IReadOnlyList<NexusModHit>> FindModsByNameAsync(NexusModsGameId game, string name, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public Task<uint?> FindMainFileAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public Task<IReadOnlyList<ArchiveHit>> LookupArchivesAsync(IReadOnlyList<string> md5s, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
        public Task<IReadOnlyList<ArchiveEntry>?> ListArchiveContentAsync(uint game, uint modId, uint fileId, CancellationToken cancellationToken) => throw new OperationCanceledException(cancellationToken);
    }
}
