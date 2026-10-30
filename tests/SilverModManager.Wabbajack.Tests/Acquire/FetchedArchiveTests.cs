using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// Which library files the app fetched on a modlist's behalf: only those get a <c>FetchedFiles</c> on the list's
/// archive row, so a file the user already had, or added themselves, stays theirs.
/// </summary>
public class FetchedArchiveTests(ITestOutputHelper helper) : AcquireTestBase<FetchedArchiveTests>(helper)
{
    private static readonly Uri Mirror = new("https://example.test/files/");
    private static readonly byte[] Content = "the archive's bytes"u8.ToArray();

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

    private static ArchiveEntry Named(DownloadState state, byte[] content, string name = "a.7z") => Entry(state, Hash64(content), content.Length, name);

    /// <summary>Adds a modlist that names <paramref name="archives"/> to the library.</summary>
    private async Task<LibraryFileId> AddModlist(params ArchiveEntry[] archives)
    {
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, ListWith(archives));
        var added = await LibraryService.AddLocalFile(path);
        return LibraryFileId.From(added.AsLibraryFile().Id);
    }

    private WabbajackModlistArchive.ReadOnly RowFor(LibraryFileId modlist, byte[] content)
    {
        var hash = Hash64(content);
        return WabbajackModlistArchive.FindByModlist(Connection.Db, modlist.Value).Single(r => r.Identity.Equals(hash));
    }

    private static ArchivePlanRow BrowserRow(ArchiveEntry entry)
        => new(entry, ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);

    private static ArchivePlanRow ManualRow(ArchiveEntry entry)
        => new(entry, ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null);

    private async Task AddToLibrary(byte[] content)
    {
        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        await LibraryService.AddLocalFile(file.Path);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition never became true");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task ADownloadTheAcquirerMadeIsMarkedFetchedForTheList()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var modlist = await AddModlist(entry);
        Jobs.Content[entry.XxHash64] = Content;

        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Automatic, null, null, null)], modlist), null, CancellationToken.None);

        var row = RowFor(modlist, Content);
        row.FetchedFiles.Should().ContainSingle();
        row.FetchedFiles.Single().Id.Should().Be(Acquirer.Plan.Rows[0].Library!.Value.Id);
    }

    [Fact]
    public async Task ADownloadWhoseBytesDoNotMatchIsNotMarked()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var modlist = await AddModlist(entry);
        Jobs.Content[entry.XxHash64] = [9, 9, 9, 9];

        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Automatic, null, null, null)], modlist), null, CancellationToken.None);

        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task APlanWithoutAListMarksNothingAndStillDownloads()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var modlist = await AddModlist(entry);
        Jobs.Content[entry.XxHash64] = Content;

        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Automatic, null, null, null)]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Have);
        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ABrowserRowThatArrivesDuringARunIsMarkedFetched()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new NexusState("SkyrimSpecialEdition", 77, 4242, null, null, null, false), Content, "mod.txt");
        var modlist = await AddModlist(entry);
        await Acquirer.Run(new ArchivePlan([BrowserRow(entry)], modlist), null, CancellationToken.None);

        await AddToLibrary(Content);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        await WaitFor(() => RowFor(modlist, Content).FetchedFiles.Any());
        RowFor(modlist, Content).FetchedFiles.Single().Id.Should().Be(Acquirer.Plan.Rows[0].Library!.Value.Id);
    }

    [Fact]
    public async Task ABrowserRowThatArrivesWhileOnlyShowingThePlanIsNotMarked()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new NexusState("SkyrimSpecialEdition", 77, 4242, null, null, null, false), Content, "mod.txt");
        var modlist = await AddModlist(entry);
        Acquirer.Show(new ArchivePlan([BrowserRow(entry)], modlist));

        await AddToLibrary(Content);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty("the user added it, no run asked for it");
    }

    [Fact]
    public async Task AManualRowThatTheUserAddsIsNotMarked()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), Content, "m.txt");
        var modlist = await AddModlist(entry);
        await Acquirer.Run(new ArchivePlan([ManualRow(entry)], modlist), null, CancellationToken.None);

        await AddToLibrary(Content);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowThatWasHeldWhenThePlanWasBuiltIsNeverMarked()
    {
        using var page = Acquirer.Attach();
        await AddToLibrary(Content);
        var held = Connection.Db;
        var entry = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var modlist = await AddModlist(entry);
        var library = ServiceProvider.GetRequiredService<SilverModManager.Wabbajack.Library.ILibraryArchiveLocator>().FindByXxHash64(held, entry.XxHash64)!.Value;

        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Have, null, null, library)], modlist), null, CancellationToken.None);

        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileMatchedFromTheInboxIsMarkedFetched()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), Content, "m.txt");
        var modlist = await AddModlist(entry);
        await Acquirer.Run(new ArchivePlan([ManualRow(entry)], modlist), null, CancellationToken.None);
        await InboxFolder.Combine("dropped.txt").WriteAllBytesAsync(Content);

        await Inbox.Scan(CancellationToken.None);

        var row = RowFor(modlist, Content);
        row.FetchedFiles.Should().ContainSingle();
        row.FetchedFiles.Single().Id.Should().Be(Acquirer.Plan.Rows[0].Library!.Value.Id);
    }

    [Fact]
    public async Task AnInboxFileNoArchiveOfThePlanWantsMarksNothing()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new ManualState(new Uri("https://example.test/m"), "get it"), Content, "m.txt");
        var modlist = await AddModlist(entry);
        await Acquirer.Run(new ArchivePlan([ManualRow(entry)], modlist), null, CancellationToken.None);
        await InboxFolder.Combine("stray.txt").WriteAllBytesAsync("a file nobody asked for"u8.ToArray());

        await Inbox.Scan(CancellationToken.None);

        RowFor(modlist, Content).FetchedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task APlanBuiltForAListCarriesTheListsId()
    {
        var modlist = await AddModlist();
        var plan = new ArchivePlan([], modlist);

        plan.Modlist.Should().Be(modlist);
        new ArchivePlan([]).Modlist.Should().BeNull();
    }

    [Fact]
    public async Task RemovingAModlistFileDeletesItsArchiveRowsAndOnlyThose()
    {
        var one = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var other = Named(new HttpState(new Uri(Mirror, "b.7z"), []), "another"u8.ToArray(), "b.7z");
        var removed = await AddModlist(one);
        await using var folder = TemporaryFileManager.CreateFolder();
        var kept = LibraryFileId.From((await LibraryService.AddLocalFile(WabbajackFixture.Write(folder.Path, ListWith(other) with { Name = "Kept" }, fileName: "kept.wabbajack"))).AsLibraryFile().Id);
        WabbajackModlistArchive.FindByModlist(Connection.Db, removed.Value).Should().HaveCount(1);

        await LibraryService.RemoveLibraryItems([LibraryItem.Load(Connection.Db, removed.Value)]);

        await WaitFor(() => !WabbajackModlistArchive.FindByModlist(Connection.Db, removed.Value).Any());
        WabbajackModlistArchive.FindByModlist(Connection.Db, kept.Value).Should().HaveCount(1);
    }

    [Fact]
    public async Task RemovingTheFetchedFileKeepsTheListsRowForTheArchive()
    {
        using var page = Acquirer.Attach();
        var entry = Named(new HttpState(new Uri(Mirror, "a.7z"), []), Content);
        var modlist = await AddModlist(entry);
        Jobs.Content[entry.XxHash64] = Content;
        await Acquirer.Run(new ArchivePlan([new ArchivePlanRow(entry, ArchiveState.Automatic, null, null, null)], modlist), null, CancellationToken.None);
        var fetched = Acquirer.Plan.Rows[0].Library!.Value;

        await LibraryService.RemoveLibraryItems([fetched.AsLibraryItem()], GarbageCollectorRunMode.RunSynchronously);

        var row = RowFor(modlist, Content);
        row.Name.Should().Be("a.7z");
        row.FetchedFiles.Should().BeEmpty();
    }
}
