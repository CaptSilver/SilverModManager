using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Library;

namespace NexusMods.Wabbajack.Tests.Acquire;

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

    [Fact]
    public async Task NoMoreThanFourDownloadsRunAtOnceAndEveryRowEndsUpHeld()
    {
        var (plan, _) = TenHttpRows();

        await Acquirer.Run(plan, null, CancellationToken.None);

        _jobs.Gauge.Peak.Should().BeInRange(2, 4);
        Acquirer.Plan.Rows.Should().OnlyContain(r => r.State == ArchiveState.Have && r.Library != null);
    }

    [Fact]
    public async Task AFailedDownloadBecomesAManualRowWithItsUrlAndTheReason()
    {
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
    public async Task ADownloadWhoseBytesDoNotMatchTheListedHashBecomesManual()
    {
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
        var content = "the archive's bytes"u8.ToArray();
        var state = new NexusState("SkyrimSpecialEdition", 77, 4242, null, null, null, false);
        var row = new ArchivePlanRow(Entry(state, Hash64(content), content.Length, "mod.txt"), ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);
        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);
        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Browser);

        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        await LibraryService.AddLocalFile(file.Path);

        await WaitFor(() => Acquirer.Plan.Rows[0].State == ArchiveState.Have);
        Acquirer.Plan.Rows[0].Library.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenNextOpensTheBrowserRowsInBatches()
    {
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
        var (first, _) = TenHttpRows();
        var browser = new ArchivePlanRow(Entry(new NexusState("Skyrim", 5, 6, null, null, null, false), Hash.From(0x5151)), ArchiveState.Browser, null, new Uri("https://www.nexusmods.com/x"), null);

        var firstRun = Acquirer.Run(first, null, CancellationToken.None);
        await Acquirer.Run(new ArchivePlan([browser]), null, CancellationToken.None);
        await firstRun;

        Acquirer.Plan.Rows.Should().ContainSingle().Which.Should().Be(browser);
    }

    [Fact]
    public async Task ManualAndUnsupportedRowsAreNotDownloaded()
    {
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
            ServiceProvider.GetRequiredService<NexusMods.Abstractions.Games.FileHashes.IFileHashesService>());
        var loadout = await CreateLoadout();
        var list = ListWith(states.Select((s, i) => Entry(s, Hash.From((ulong)(i + 1) * 0x1111), 10, $"{i}.7z")).ToArray());
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
