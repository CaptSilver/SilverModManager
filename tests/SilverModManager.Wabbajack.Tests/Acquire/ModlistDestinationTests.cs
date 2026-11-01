using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// The game a modlist's files are for: the list says it, so every path that brings one of its files into the library
/// (the list itself, a download, the inbox, a file already held) records that game on the item.
/// </summary>
public class ModlistDestinationTests(ITestOutputHelper helper) : AcquireTestBase<ModlistDestinationTests>(helper)
{
    private static readonly GameId SkyrimSe = GameId.From("CreationEngine.SkyrimSE");
    private static readonly GameId SkyrimVr = GameId.From("CreationEngine.SkyrimVR");
    private static readonly GameId OtherGame = GameId.From("CreationEngine.Fallout4");

    private static readonly Uri Mirror = new("https://example.test/files/");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<OpenedUris>()
            .AddSingleton<IOSInterop>(provider => provider.GetRequiredService<OpenedUris>().Interop)
            .AddSingleton<IArchiveDownloadJobs>(provider => new FakeDownloadJobs(provider));
    }

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private FakeDownloadJobs Jobs => (FakeDownloadJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();
    private ArchiveInbox Inbox => ServiceProvider.GetRequiredService<ArchiveInbox>();
    private AbsolutePath InboxFolder => ServiceProvider.GetRequiredService<WabbajackPaths>().Inbox;

    private async Task<LibraryFileId> AddModlistFor(string gameType, string name, IAcquisitionContext acquisition, params ArchiveEntry[] archives)
    {
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, ListWith(archives) with { Name = name, GameType = gameType });
        var added = await LibraryService.AddLocalFile(path, acquisition);
        return LibraryFileId.From(added.AsLibraryFile().Id);
    }

    private IReadOnlySet<GameId> GamesOf(EntityId item) => LibraryDestinations.GamesOf(LibraryItem.Load(Connection.Db, item));

    private bool IsUnknown(EntityId item) => LibraryItem.Load(Connection.Db, item).Contains(LibraryItem.DestinationUnknown);

    private static ArchiveEntry Named(DownloadState state, byte[] content, string name) => Entry(state, Hash64(content), content.Length, name);

    private EntityId HeldFile(byte[] content)
        => ServiceProvider.GetRequiredService<ILibraryArchiveLocator>().FindTopLevelByXxHash64(Connection.Db, Hash64(content))!.Value.Id;

    private async Task<EntityId> AddFileFor(byte[] content, GameId game)
    {
        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        return (await LibraryService.AddLocalFile(file.Path, new GameAcquisition(game))).Id;
    }

    [Fact]
    public void ModlistAcquisition_NoModlist_IsUnknown()
        => new ModlistAcquisition(null).Destination(Connection.Db).IsUnknown.Should().BeTrue();

    [Fact]
    public async Task ModlistAcquisition_ListWithAGame_IsThatGame()
    {
        var list = await AddModlistFor("SkyrimVR", "A VR list", UnassignedAcquisition.Instance);

        new ModlistAcquisition(list).Destination(Connection.Db).Should().Be(Destination.Of(SkyrimVr));
    }

    [Fact]
    public async Task ModlistAcquisition_ListForAGameTheAppHasNoTitleFor_IsUnknown()
    {
        var list = await AddModlistFor("Witcher3", "A list for another game", UnassignedAcquisition.Instance);

        new ModlistAcquisition(list).Destination(Connection.Db).IsUnknown.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recogniser_ReturnsTheListsGame(bool addedForAnotherGame)
    {
        IAcquisitionContext acquisition = addedForAnotherGame ? new GameAcquisition(OtherGame) : UnassignedAcquisition.Instance;

        var list = await AddModlistFor("SkyrimVR", "Drops with its game", acquisition);

        GamesOf(list.Value).Should().BeEquivalentTo([SkyrimVr]);
        IsUnknown(list.Value).Should().BeFalse();
    }

    [Fact]
    public async Task Recogniser_ListForAGameTheAppHasNoTitleFor_LeavesTheAcquisitionsAnswer()
    {
        var list = await AddModlistFor("Witcher3", "No title", UnassignedAcquisition.Instance);

        GamesOf(list.Value).Should().BeEmpty();
        IsUnknown(list.Value).Should().BeTrue();
    }

    [Fact]
    public async Task Recogniser_InheritedFetchedFile_GainsTheNewListsGame()
    {
        var content = "an archive the first list fetched"u8.ToArray();
        var archive = Named(new HttpState(new Uri(Mirror, "a.7z"), []), content, "a.7z");
        var first = await AddModlistFor("SkyrimSpecialEdition", "First", UnassignedAcquisition.Instance, archive);
        var held = await AddFileFor(content, SkyrimSe);
        using (var tx = Connection.BeginTransaction())
        {
            var row = WabbajackModlistArchive.FindByModlist(Connection.Db, first.Value).Single();
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, held);
            await tx.Commit();
        }

        await AddModlistFor("SkyrimVR", "Second", UnassignedAcquisition.Instance, archive);

        GamesOf(held).Should().BeEquivalentTo([SkyrimSe, SkyrimVr]);
    }

    [Fact]
    public async Task Acquirer_Download_RecordsTheListsGame()
    {
        using var page = Acquirer.Attach();
        var content = new byte[] { 4, 5, 6, 7 };
        var list = await AddModlistFor("SkyrimVR", "VR list", UnassignedAcquisition.Instance);
        var row = new ArchivePlanRow(Named(new HttpState(new Uri(Mirror, "d.7z"), []), content, "d.7z"), ArchiveState.Automatic, null, null, null);
        Jobs.Content[Hash64(content)] = content;

        await Acquirer.Run(new ArchivePlan([row], list), null, CancellationToken.None);

        var downloaded = Acquirer.Plan.Rows.Single().Library!.Value.Id;
        GamesOf(downloaded).Should().BeEquivalentTo([SkyrimVr]);
        IsUnknown(downloaded).Should().BeFalse();
    }

    [Fact]
    public async Task Acquirer_PlanWithoutModlist_RecordsUnknown()
    {
        using var page = Acquirer.Attach();
        var content = new byte[] { 8, 9, 10, 11 };
        var row = new ArchivePlanRow(Named(new HttpState(new Uri(Mirror, "e.7z"), []), content, "e.7z"), ArchiveState.Automatic, null, null, null);
        Jobs.Content[Hash64(content)] = content;

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        var downloaded = Acquirer.Plan.Rows.Single().Library!.Value.Id;
        GamesOf(downloaded).Should().BeEmpty();
        IsUnknown(downloaded).Should().BeTrue();
    }

    [Fact]
    public async Task Inbox_MatchedFile_RecordsTheListsGame()
    {
        using var page = Acquirer.Attach();
        var wanted = "a file the VR list wants"u8.ToArray();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), wanted, "wanted.txt");
        var list = await AddModlistFor("SkyrimVR", "Inbox list", UnassignedAcquisition.Instance, entry);
        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null)], list), null, CancellationToken.None);
        await InboxFolder.Combine("wanted.txt").WriteAllBytesAsync(wanted);

        await Inbox.Scan(CancellationToken.None);

        var held = HeldFile(wanted);
        GamesOf(held).Should().BeEquivalentTo([SkyrimVr]);
        IsUnknown(held).Should().BeFalse();
    }

    [Fact]
    public async Task Inbox_UnmatchedFile_IsUnassigned()
    {
        using var page = Acquirer.Attach();
        var wanted = "a file the list wants"u8.ToArray();
        var stray = "a file nobody asked for"u8.ToArray();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), wanted, "wanted.txt");
        var list = await AddModlistFor("SkyrimVR", "Inbox list", UnassignedAcquisition.Instance, entry);
        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null)], list), null, CancellationToken.None);
        await InboxFolder.Combine("stray.txt").WriteAllBytesAsync(stray);

        await Inbox.Scan(CancellationToken.None);

        var held = HeldFile(stray);
        GamesOf(held).Should().BeEmpty();
        IsUnknown(held).Should().BeTrue();
    }

    [Fact]
    public async Task Inbox_FileForAPlanWithoutAModlist_IsUnassigned()
    {
        using var page = Acquirer.Attach();
        var wanted = "a file the bare plan wants"u8.ToArray();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), wanted, "wanted.txt");
        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null)]), null, CancellationToken.None);
        await InboxFolder.Combine("wanted.txt").WriteAllBytesAsync(wanted);

        await Inbox.Scan(CancellationToken.None);

        IsUnknown(HeldFile(wanted)).Should().BeTrue();
    }

    [Fact]
    public async Task FetchedArchives_Mark_AddsEachMarkedListsGameToAHeldFile()
    {
        var content = "a file the user already had for SE"u8.ToArray();
        var archive = Named(new HttpState(new Uri(Mirror, "h.7z"), []), content, "h.7z");
        var held = await AddFileFor(content, SkyrimSe);
        await AddModlistFor("SkyrimVR", "VR list", UnassignedAcquisition.Instance, archive);
        await AddModlistFor("Fallout4", "FO4 list", UnassignedAcquisition.Instance, archive);

        await FetchedArchives.Mark(Connection, archive.XxHash64, LibraryFile.Load(Connection.Db, held));

        GamesOf(held).Should().BeEquivalentTo([SkyrimSe, SkyrimVr, OtherGame]);
    }

    [Fact]
    public async Task FetchedArchives_Mark_ListForAGameWithoutATitle_AddsNothing()
    {
        var content = "a file for a list nobody has a title for"u8.ToArray();
        var archive = Named(new HttpState(new Uri(Mirror, "w.7z"), []), content, "w.7z");
        var held = await AddFileFor(content, SkyrimSe);
        await AddModlistFor("Witcher3", "No title", UnassignedAcquisition.Instance, archive);

        await FetchedArchives.Mark(Connection, archive.XxHash64, LibraryFile.Load(Connection.Db, held));

        GamesOf(held).Should().BeEquivalentTo([SkyrimSe]);
    }

    [Fact]
    public async Task Acquirer_ExpectedNexusFile_CarriesTheListsGame()
    {
        var list = await AddModlistFor("SkyrimVR", "VR list", UnassignedAcquisition.Instance);
        var row = new ArchivePlanRow(
            Entry(new NexusState("SkyrimSpecialEdition", 5, 6, null, null, null, false), Xx64.From(0x5151)),
            ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);
        var expected = ServiceProvider.GetRequiredService<IExpectedNexusDownloads>();

        await Acquirer.Run(new ArchivePlan([row], list), null, CancellationToken.None);

        expected.TryGetExpectedGame(GameDomain.From("skyrimspecialedition"), ModId.From(5), FileId.From(6), out var game).Should().BeTrue();
        game.Should().Be(SkyrimVr);
    }
}
