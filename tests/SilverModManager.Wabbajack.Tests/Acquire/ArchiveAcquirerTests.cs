using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

public class ArchiveAcquirerTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveAcquirerTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<OpenedUris>()
            .AddSingleton<IOSInterop>(provider => provider.GetRequiredService<OpenedUris>().Interop)
            .AddSingleton<IArchiveDownloadJobs>(provider => new FakeDownloadJobs(provider));
    }

    private FakeDownloadJobs _jobs => (FakeDownloadJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>();

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();

    private static ArchivePlanRow AutomaticRow(DownloadState state, byte[] content, string name)
        => new(Entry(state, Hash64(content), content.Length, name), ArchiveState.Automatic, null, null, null);

    private static readonly Uri Mirror = new("https://example.test/files/");

    private (ArchivePlan Plan, byte[][] Contents) TenHttpRows()
    {
        var contents = Enumerable.Range(0, 10).Select(i => new byte[] { (byte)i, 9, 9, 9 }).ToArray();
        var rows = contents.Select((c, i) => AutomaticRow(new HttpState(new Uri(Mirror, $"{i}.7z"), []), c, $"{i}.7z")).ToArray();
        foreach (var (content, i) in contents.Select((c, i) => (c, i))) _jobs.Content[Hash64(content)] = content;
        return (new ArchivePlan(rows), contents);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task TheDownloadsAtOnceSettingSetsHowManyArchivesTransferTogether(int atOnce)
    {
        ServiceProvider.GetRequiredService<ISettingsManager>().Update<DownloadSettings>(_ => new DownloadSettings { MaxParallelDownloads = atOnce });
        using var page = Acquirer.Attach();
        var contents = Enumerable.Range(0, 20).Select(i => new byte[] { (byte)i, 8, 8, 8 }).ToArray();
        var rows = contents.Select((c, i) => AutomaticRow(new HttpState(new Uri(Mirror, $"{i}.7z"), []), c, $"{i}.7z")).ToArray();
        foreach (var content in contents) _jobs.Content[Hash64(content)] = content;
        var release = new TaskCompletionSource();
        _jobs.Gate = release.Task;

        var run = Acquirer.Run(new ArchivePlan(rows), null, CancellationToken.None);
        // Every slot is taken while the jobs are held back, so the gauge reads the gate size itself.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (_jobs.Gauge.Current < atOnce && DateTime.UtcNow < deadline) await Task.Delay(5, TestContext.Current.CancellationToken);
        _jobs.Gauge.Current.Should().Be(atOnce);
        release.SetResult();
        await run;

        _jobs.Gauge.Peak.Should().Be(atOnce);
        Acquirer.Plan.Rows.Should().OnlyContain(r => r.State == ArchiveState.Have && r.Library != null);
    }

    [Fact]
    public async Task AFailedDownloadBecomesAManualRowWithItsUrlAndTheReason()
    {
        using var page = Acquirer.Attach();
        var (plan, contents) = TenHttpRows();
        _jobs.Failures[Hash64(contents[3])] = new IOException("connection reset");

        await Acquirer.Run(plan, null, CancellationToken.None);

        var failed = Acquirer.Plan.Rows[3];
        failed.State.Should().Be(ArchiveState.Manual);
        failed.Reason.Should().Contain("connection reset");
        failed.Page.Should().Be(new Uri(Mirror, "3.7z"));
        Acquirer.Plan.Rows.Where((_, i) => i != 3).Should().OnlyContain(r => r.State == ArchiveState.Have);
    }

    [Fact]
    public async Task ADownloadFromAFileHostThatFailsLeavesTheUserTheHostsPage()
    {
        using var page = Acquirer.Attach();
        var mega = new Uri("https://mega.nz/file/AbCdEf12#" + new string('A', 43));
        var other = new Uri("https://example.test/page");
        var states = new DownloadState[] { new MegaState(mega), new GoogleDriveState("abc123"), new MediaFireState(other), new ModDbState(other) };
        var contents = states.Select((_, i) => new byte[] { (byte)i, 5, 5 }).ToArray();
        foreach (var content in contents) _jobs.Failures[Hash64(content)] = new IOException("the host said no");
        var rows = states.Select((state, i) => AutomaticRow(state, contents[i], $"{i}.7z")).ToArray();

        await Acquirer.Run(new ArchivePlan(rows), null, CancellationToken.None);

        Acquirer.Plan.Rows.Select(r => r.State).Should().OnlyContain(s => s == ArchiveState.Manual);
        Acquirer.Plan.Rows.Select(r => r.Reason).Should().OnlyContain(r => r == "the host said no");
        Acquirer.Plan.Rows.Select(r => r.Page).Should().Equal(mega, new Uri("https://drive.google.com/uc?id=abc123&export=download"), other, other);
    }

    [Fact]
    public async Task ADownloadWhoseBytesDoNotMatchTheListedHashBecomesManual()
    {
        using var page = Acquirer.Attach();
        var content = new byte[] { 1, 2, 3, 4 };
        var row = AutomaticRow(new HttpState(new Uri(Mirror, "a.7z"), []), content, "a.7z");
        _jobs.Content[row.Archive.XxHash64] = [9, 9, 9, 9];

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        Acquirer.Plan.Rows[0].Reason.Should().Be("hash mismatch");
    }

    [Fact]
    public async Task ProgressCountsTheAutomaticRowsAsTheyFinish()
    {
        var (plan, _) = TenHttpRows();
        var reports = new List<AcquireProgress>();

        await Acquirer.Run(plan, new SynchronousProgress(reports.Add), CancellationToken.None);

        reports.Should().NotBeEmpty();
        reports.Last().Should().Be(new AcquireProgress(10, 10));
    }

    private sealed class SynchronousProgress(Action<AcquireProgress> report) : IProgress<AcquireProgress>
    {
        public void Report(AcquireProgress value) => report(value);
    }

    [Fact]
    public async Task ABrowserRowBecomesHeldWhenALibraryFileWithTheRightHashAppears()
    {
        using var page = Acquirer.Attach();
        var content = "the archive's bytes"u8.ToArray();
        var state = new NexusState("SkyrimSpecialEdition", 77, 4242, null, null, null, false);
        var row = new ArchivePlanRow(Entry(state, Hash64(content), content.Length, "mod.txt"), ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);
        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Browser);

        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        await LibraryService.AddLocalFile(file.Path, UnassignedAcquisition.Instance);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].Library.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenNextOpensTheBrowserRowsInBatches()
    {
        using var page = Acquirer.Attach();
        var plan = await BrowserPlan(Enumerable.Range(0, 9).Select(i => new NexusState("Skyrim", 100 + i, 200 + i, null, null, null, false)));
        await Acquirer.Run(plan, null, CancellationToken.None);

        var opened = ServiceProvider.GetRequiredService<OpenedUris>();

        Acquirer.OpenNext(5).Should().Be(5);
        opened.Opened.Should().HaveCount(5);
        Acquirer.OpenNext(5).Should().Be(4);
        opened.Opened.Should().HaveCount(9);
        Acquirer.OpenNext(5).Should().Be(0);
        opened.Opened.Should().OnlyHaveUniqueItems();
        opened.Opened.Should().OnlyContain(u => u.ToString().Contains("/skyrim/mods/"));
        Acquirer.Plan.Rows.Should().OnlyContain(r => r.Opened);
    }

    [Fact]
    public async Task ABrowserRowIsExpectedFromTheNxmHandlerUntilThePlanIsReleased()
    {
        var plan = await BrowserPlan([new NexusState("Skyrim", 5, 6, null, null, null, false)]);
        var expected = ServiceProvider.GetRequiredService<IExpectedNexusDownloads>();
        expected.IsExpected(GameDomain.From("skyrim"), ModId.From(5), FileId.From(6)).Should().BeFalse();

        await Acquirer.Run(plan, null, CancellationToken.None);

        expected.IsExpected(GameDomain.From("skyrim"), ModId.From(5), FileId.From(6)).Should().BeTrue();
        expected.IsExpected(GameDomain.From("skyrimspecialedition"), ModId.From(5), FileId.From(6)).Should().BeFalse();

        Acquirer.Release();
        expected.IsExpected(GameDomain.From("skyrim"), ModId.From(5), FileId.From(6)).Should().BeFalse();
    }

    [Fact]
    public async Task ShowingAPlanStartsNoDownloadOpensNoPageAndExpectsNoFile()
    {
        using var page = Acquirer.Attach();
        var (automatic, _) = TenHttpRows();
        var browser = await BrowserPlan([new NexusState("Skyrim", 5, 6, null, null, null, false)]);
        var opened = ServiceProvider.GetRequiredService<OpenedUris>();
        var expected = ServiceProvider.GetRequiredService<IExpectedNexusDownloads>();

        Acquirer.Show(new ArchivePlan(automatic.Rows.Concat(browser.Rows).ToArray()));

        Acquirer.Plan.Rows.Should().HaveCount(11);
        Acquirer.Plan.Rows.Take(10).Should().OnlyContain(r => r.State == ArchiveState.Automatic);
        _jobs.Requested.Should().BeEmpty();
        opened.Opened.Should().BeEmpty();
        expected.IsExpected(GameDomain.From("skyrim"), ModId.From(5), FileId.From(6)).Should().BeFalse();
    }

    [Fact]
    public async Task ARunAfterShowingThePlanExpectsItsBrowserRows()
    {
        var plan = await BrowserPlan([new NexusState("Skyrim", 5, 6, null, null, null, false)]);
        var expected = ServiceProvider.GetRequiredService<IExpectedNexusDownloads>();
        Acquirer.Show(plan);

        await Acquirer.Run(plan, null, CancellationToken.None);

        expected.IsExpected(GameDomain.From("skyrim"), ModId.From(5), FileId.From(6)).Should().BeTrue();
    }

    [Fact]
    public async Task DownloadsOfAReplacedPlanDoNotTouchTheNewPlan()
    {
        using var page = Acquirer.Attach();
        var (first, _) = TenHttpRows();
        var browser = new ArchivePlanRow(Entry(new NexusState("Skyrim", 5, 6, null, null, null, false), Xx64.From(0x5151)), ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);

        var firstRun = Acquirer.Run(first, null, CancellationToken.None);
        await Acquirer.Run(new ArchivePlan([browser]), null, CancellationToken.None);
        await firstRun;

        Acquirer.Plan.Rows.Should().ContainSingle().Which.Should().Be(browser);
    }

    private static ArchivePlanRow ForumRow(ArchiveState state, byte[] content, string name = "f.7z")
        => new(Entry(new LoversLabState(new Uri("https://www.loverslab.com/files/file/1-x/?do=download&csrfKey=secret"), name, 7, false), Hash64(content), content.Length, name),
            state, state == ArchiveState.SignIn ? "Sign in to LoversLab to download this" : null, new Uri("https://www.loverslab.com/files/file/1-x/"), null);

    [Fact]
    public async Task ASignInRowIsNotDownloadedByARun()
    {
        using var page = Acquirer.Attach();
        var row = ForumRow(ArchiveState.SignIn, [1, 2, 3]);

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        _jobs.Requested.Should().BeEmpty();
        Acquirer.Plan.Rows[0].Should().Be(row);
    }

    [Fact]
    public async Task AFailingForumDownloadBecomesManualWithTheJobsReasonAndTheQueryFreePage()
    {
        using var page = Acquirer.Attach();
        var content = new byte[] { 4, 5, 6 };
        var row = ForumRow(ArchiveState.Automatic, content);
        _jobs.Failures[row.Archive.XxHash64] = new IOException("Your LoversLab session expired. Sign in again, then download again.");

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        var failed = Acquirer.Plan.Rows[0];
        failed.State.Should().Be(ArchiveState.Manual);
        failed.Reason.Should().Be("Your LoversLab session expired. Sign in again, then download again.");
        failed.Page.Should().Be(new Uri("https://www.loverslab.com/files/file/1-x/"));
    }

    [Fact]
    public async Task AFileDroppedInTheLibraryCreditsASignInRow()
    {
        using var page = Acquirer.Attach();
        var content = "the forum archive's bytes"u8.ToArray();
        await Acquirer.Run(new ArchivePlan([ForumRow(ArchiveState.SignIn, content)]), null, CancellationToken.None);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.SignIn);

        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        await LibraryService.AddLocalFile(file.Path, UnassignedAcquisition.Instance);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].Library.Should().NotBeNull();
    }

    [Fact]
    public async Task ManualAndUnsupportedRowsAreNotDownloaded()
    {
        using var page = Acquirer.Attach();
        var manual = new ArchivePlanRow(Entry(new ManualState(new Uri("https://example.test/m"), "get it")), ArchiveState.Manual, "get it", new Uri("https://example.test/m"), null);
        await Acquirer.Run(new ArchivePlan([manual]), null, CancellationToken.None);

        _jobs.Requested.Should().BeEmpty();
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
    }

    private async Task<ArchivePlan> BrowserPlan(IEnumerable<NexusState> states)
    {
        var inventory = new ArchiveInventory(
            ServiceProvider.GetRequiredService<ILibraryArchiveLocator>(),
            Fakes.Login(premium: false),
            new FakeMappingCache(),
            Connection,
            FileStore,
            ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileIdentityResolver>(),
            ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileHashesService>(),
            Fakes.Forums());
        var loadout = await CreateLoadout();
        var list = ListWith(states.Select((s, i) => Entry(s, Xx64.From((ulong)(i + 1) * 0x1111), 10, $"{i}.7z")).ToArray());
        var plan = await inventory.Build(list, Connection.Db, loadout);
        plan.Rows.Should().OnlyContain(r => r.State == ArchiveState.Browser);
        return plan;
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
}
