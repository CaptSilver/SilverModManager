using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Tests.Acquire.Hosts;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>What a run tells the log about an archive it could not fetch: one Warning each, naming where to look.</summary>
public class ArchiveAcquirerLoggingTests(ITestOutputHelper helper) : AcquireTestBase<ArchiveAcquirerLoggingTests>(helper)
{
    private static readonly Uri ModDbPage = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch?token=secret");
    private static readonly Uri ModDbPageBare = new("https://www.moddb.com/mods/cool-mod/addons/cool-patch");

    private readonly ListLogger<ArchiveAcquirer> _acquirerLog = new();
    private readonly ListLogger<ModDbDownloadJob> _jobLog = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddSingleton<ILogger<ArchiveAcquirer>>(_acquirerLog)
            .AddSingleton<IArchiveDownloadJobs>(provider => new ModDbOrFakeJobs(provider, _jobLog));
    }

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();

    private static ArchivePlanRow AutomaticRow(DownloadState state, byte[] content, string name)
        => new(Entry(state, Hash64(content), content.Length, name), ArchiveState.Automatic, null, null, null);

    private string[] Warnings() => [.. _acquirerLog.Messages(LogLevel.Warning), .. _jobLog.Messages(LogLevel.Warning)];

    [Fact]
    public async Task AFailedHostDownloadIsWarnedAboutOnceAcrossTheJobAndTheRun()
    {
        using var page = Acquirer.Attach();
        var row = AutomaticRow(new ModDbState(ModDbPage), [1, 2, 3], "Cool_Mod.7z");

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].State.Should().Be(ArchiveState.Manual);
        var warning = Warnings().Should().ContainSingle().Subject;
        warning.Should().Contain("Cool_Mod.7z").And.Contain("ModDB").And.Contain(ModDbPageBare.ToString()).And.NotContain("secret");
    }

    [Fact]
    public async Task AFailureOutsideAHostJobIsStillWarnedAboutOnce()
    {
        using var page = Acquirer.Attach();
        var row = AutomaticRow(new HttpState(new Uri("https://example.test/a.7z"), []), [1, 2, 3], "a.7z");
        ((ModDbOrFakeJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>()).Fake.Failures[row.Archive.XxHash64] = new IOException("connection reset");

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        Warnings().Should().ContainSingle().Which.Should().Contain("a.7z");
    }

    [Fact]
    public async Task ABodyThatDoesNotMatchTheListedHashIsWarnedAboutWithBothHashesAndThePageWithoutItsQuery()
    {
        using var page = Acquirer.Attach();
        var row = AutomaticRow(new HttpState(new Uri("https://example.test/a.7z?key=secret"), []), [1, 2, 3, 4], "a.7z");
        var served = new byte[] { 9, 9, 9, 9 };
        var fake = ((ModDbOrFakeJobs)ServiceProvider.GetRequiredService<IArchiveDownloadJobs>()).Fake;
        fake.Content[row.Archive.XxHash64] = served;

        await Acquirer.Run(new ArchivePlan([row]), null, CancellationToken.None);

        Acquirer.Plan.Rows[0].Reason.Should().Be("hash mismatch");
        var warning = Warnings().Should().ContainSingle().Subject;
        warning.Should().Contain("a.7z").And.Contain("https://example.test/a.7z")
            .And.Contain(row.Archive.XxHash64.ToString()).And.Contain(Hash64(served).ToString())
            .And.NotContain("secret");
    }

    /// <summary>A ModDB row gets the real job against a host that answers 404; anything else goes to the fake jobs.</summary>
    private sealed class ModDbOrFakeJobs(IServiceProvider provider, ILogger<ModDbDownloadJob> jobLog) : IArchiveDownloadJobs
    {
        public FakeDownloadJobs Fake { get; } = new(provider);

        public async Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
        {
            if (row.Archive.State is not ModDbState moddb) return await Fake.Create(row, destination, cancellationToken);
            var job = new ModDbDownloadJob
            {
                Client = new HttpClient(new FakeHost((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))),
                Logger = jobLog,
                Throttle = new HostThrottle(),
                Url = moddb.Url,
                FileName = row.Archive.Name,
                Destination = destination,
            };
            return provider.GetRequiredService<IJobMonitor>().Begin<ModDbDownloadJob, AbsolutePath>(job);
        }
    }
}
