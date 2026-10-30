using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Cli;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>wabbajack list</c>, <c>download</c>, <c>status</c> and <c>cancel</c> against a doubled modlist catalog,
/// planner and acquirer: what they parse, refuse and print.
/// </summary>
public class WabbajackBatchVerbTests(IServiceProvider provider)
{
    private static readonly ModlistInfo Skyrim = new(LibraryFileId.From(EntityId.From(0xA0A)), "Skyrim list", "1.2", "SkyrimSpecialEdition", 40, "skyrim-list.wabbajack");
    private static readonly ModlistInfo Fallout = new(LibraryFileId.From(EntityId.From(0xB0B)), "Fallout list", "3.0", "Fallout4", 12, "fallout.wabbajack");

    private IModlistCatalog Catalog => provider.GetRequiredService<IModlistCatalog>();
    private IArchiveAcquirer Acquirer => provider.GetRequiredService<IArchiveAcquirer>();
    private IModlistPlanner Planner => provider.GetRequiredService<IModlistPlanner>();

    private void Arrange(AcquireStatus? status = null, ArchivePlan? plan = null)
    {
        Catalog.ClearReceivedCalls();
        Acquirer.ClearReceivedCalls();
        Planner.ClearReceivedCalls();
        Catalog.List().Returns([Skyrim, Fallout]);
        Catalog.Find(Arg.Any<string>()).Returns(call => call.Arg<string>() switch
        {
            "Skyrim list" => Skyrim,
            "Fallout list" => Fallout,
            var other => throw new ModlistNotKnownException($"'{other}' is not a modlist the app knows."),
        });
        Acquirer.Status.Returns(status);
        Acquirer.Plan.Returns(plan ?? new ArchivePlan([]));
        Acquirer.Run(Arg.Any<ArchivePlan>(), Arg.Any<IProgress<AcquireProgress>?>(), Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource().Task);
        Planner.BuildPlan(Arg.Any<LibraryFileId>(), Arg.Any<SilverModManager.Sdk.Loadouts.LoadoutId?>(), Arg.Any<IProgress<PlanProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(MixedPlan() with { Modlist = call.Arg<LibraryFileId>() }));
    }

    private static ArchivePlanRow Row(ArchiveState state, string name, string? reason = null)
        => new(new ArchiveEntry(FileIdentity.XxHash64(NexusMods.Hashing.xxHash3.Hash.From((ulong)name.GetHashCode())), 100, name, "", new HttpState(new Uri("https://example.test/" + name), [])), state, reason, null, null);

    private static ArchivePlan MixedPlan() => new(
    [
        Row(ArchiveState.Have, "have.7z"), Row(ArchiveState.Automatic, "auto1.7z"), Row(ArchiveState.Automatic, "auto2.7z"),
        Row(ArchiveState.Browser, "nexus.7z"), Row(ArchiveState.Manual, "hand.7z"),
    ]);

    private async Task<(int ExitCode, LoggingRenderer Renderer)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static string[] Lines(LoggingRenderer renderer)
        => renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments.Cast<object>().ToArray()).TrimEnd('\n')).ToArray();

    [Fact]
    public async Task ListPrintsEveryModlistWithItsIdAndMarksTheOneDownloading()
    {
        Arrange(status: new AcquireStatus(Skyrim.Id, true, 1, 2, 0, TimeSpan.Zero, []));

        var (exitCode, renderer) = await Run("wabbajack", "list");

        exitCode.Should().Be(0);
        var rows = renderer.LastTable.Rows.Select(r => r.OfType<Text>().Select(t => t.Template).ToArray()).ToArray();
        rows.Should().HaveCount(2);
        rows[0].Should().Equal(Skyrim.HexId, "Skyrim list", "1.2", "SkyrimSpecialEdition", "40", "downloading");
        rows[1].Should().Equal(Fallout.HexId, "Fallout list", "3.0", "Fallout4", "12", "");
    }

    [Fact]
    public async Task DownloadStartsTheBatchAndPrintsTheCountsWithoutWaitingForIt()
    {
        Arrange();

        var (exitCode, renderer) = await Run("wabbajack", "download", "--modlist", "Skyrim list");

        exitCode.Should().Be(0);
        Lines(renderer).Should().ContainSingle().Which.Should()
            .Contain("Started downloading 'Skyrim list'")
            .And.Contain("2 to download automatically").And.Contain("1 to fetch in the browser").And.Contain("1 to get by hand").And.Contain("1 already there");
        await Acquirer.Received(1).Run(Arg.Is<ArchivePlan>(p => p.Modlist == Skyrim.Id), Arg.Any<IProgress<AcquireProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadWithoutTheWindowIsRefusedBecauseTheBatchWouldEndWithTheCommand()
    {
        Arrange();
        // A headless main: the same verbs and doubles, but no WindowHost.
        var headless = new ServiceCollection()
            .AddLogging()
            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddWabbajackVerbs()
            .AddSingleton(Catalog)
            .AddSingleton(Acquirer)
            .AddSingleton(Planner)
            .AddSingleton(Substitute.For<SilverModManager.Sdk.Settings.ISettingsManager>())
            .AddSingleton<ModlistDownloads>()
            .BuildServiceProvider();
        var renderer = new LoggingRenderer();

        var exitCode = await headless.GetRequiredService<CommandLineConfigurator>().RunAsync(["wabbajack", "download", "--modlist", "Skyrim list"], renderer, CancellationToken.None);

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        string.Join("\n", Lines(renderer)).Should().Contain("needs the SilverModManager window to be open");
        await Acquirer.DidNotReceive().Run(Arg.Any<ArchivePlan>(), Arg.Any<IProgress<AcquireProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadTakesTheShortOptionToo()
    {
        Arrange();

        var (exitCode, _) = await Run("wabbajack", "download", "-m", "Fallout list");

        exitCode.Should().Be(0);
        await Acquirer.Received(1).Run(Arg.Is<ArchivePlan>(p => p.Modlist == Fallout.Id), Arg.Any<IProgress<AcquireProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadSaysSoWhenTheListIsAlreadyRunningAndStartsNothing()
    {
        Arrange(status: new AcquireStatus(Skyrim.Id, true, 1, 2, 0, TimeSpan.Zero, []), plan: MixedPlan() with { Modlist = Skyrim.Id });

        var (exitCode, renderer) = await Run("wabbajack", "download", "--modlist", "Skyrim list");

        exitCode.Should().Be(0);
        Lines(renderer).Should().ContainSingle().Which.Should().Contain("already running");
        await Acquirer.DidNotReceiveWithAnyArgs().Run(default!, default, default);
    }

    [Fact]
    public async Task DownloadRefusesAModlistTheAppDoesNotKnow()
    {
        Arrange();

        var (exitCode, renderer) = await Run("wabbajack", "download", "--modlist", "Nonexistent");

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        string.Join("\n", Lines(renderer)).Should().Contain("'Nonexistent' is not a modlist the app knows");
        await Acquirer.DidNotReceiveWithAnyArgs().Run(default!, default, default);
    }

    [Fact]
    public async Task DownloadRefusesAnotherListWhileOneIsRunning()
    {
        Arrange(status: new AcquireStatus(Skyrim.Id, true, 1, 2, 0, TimeSpan.Zero, []));

        var (exitCode, renderer) = await Run("wabbajack", "download", "--modlist", "Fallout list");

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        string.Join("\n", Lines(renderer)).Should().Contain("Skyrim list");
        await Acquirer.DidNotReceiveWithAnyArgs().Run(default!, default, default);
    }

    [Fact]
    public async Task DownloadWithoutAModlistIsAUsageError()
    {
        Arrange();

        var (exitCode, _) = await Run("wabbajack", "download");

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        await Acquirer.DidNotReceiveWithAnyArgs().Run(default!, default, default);
    }

    [Fact]
    public async Task StatusBeforeAnyBatchSaysNoneHasRun()
    {
        Arrange();

        var (exitCode, renderer) = await Run("wabbajack", "status");

        exitCode.Should().Be(0);
        Lines(renderer).Should().ContainSingle().Which.Should().Contain("No download batch has run");
    }

    [Fact]
    public async Task StatusShowsProgressBytesWaitingArchivesAndFailures()
    {
        var failed = new[] { new DownloadFailure("bad.7z", "connection reset"), new DownloadFailure("worse.7z", "hash mismatch") };
        Arrange(
            status: new AcquireStatus(Skyrim.Id, true, 3, 10, 5_000_000, TimeSpan.FromSeconds(5), failed),
            plan: MixedPlan() with { Modlist = Skyrim.Id });

        var (exitCode, renderer) = await Run("wabbajack", "status");

        exitCode.Should().Be(0);
        var lines = Lines(renderer);
        lines.Should().Contain("Downloading 'Skyrim list': running");
        lines.Should().Contain("Archives: 3 of 10 done");
        lines.Should().Contain(l => l.StartsWith("Downloaded: ") && l.Contains("/s"));
        lines.Should().Contain("Waiting on the browser: 1");
        lines.Should().Contain("Waiting on you (by hand): 1");
        lines.Should().Contain("  bad.7z: connection reset").And.Contain("  worse.7z: hash mismatch");
    }

    [Fact]
    public async Task StatusForAModlistThatHasNoBatchSaysSo()
    {
        Arrange(status: new AcquireStatus(Skyrim.Id, true, 3, 10, 0, TimeSpan.Zero, []));

        var (exitCode, renderer) = await Run("wabbajack", "status", "--modlist", "Fallout list");

        exitCode.Should().Be(0);
        Lines(renderer).Should().ContainSingle().Which.Should().Contain("No download batch has run for 'Fallout list'");
    }

    [Fact]
    public async Task StatusRefusesAModlistTheAppDoesNotKnow()
    {
        Arrange();

        var (exitCode, renderer) = await Run("wabbajack", "status", "-m", "Nonexistent");

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        string.Join("\n", Lines(renderer)).Should().Contain("not a modlist the app knows");
    }

    [Fact]
    public async Task CancelStopsARunningBatch()
    {
        Arrange(status: new AcquireStatus(Skyrim.Id, true, 3, 10, 0, TimeSpan.Zero, []));

        var (exitCode, renderer) = await Run("wabbajack", "cancel");

        exitCode.Should().Be(0);
        Acquirer.Received(1).Cancel();
        Lines(renderer).Should().ContainSingle().Which.Should().Contain("Stopping");
    }

    [Fact]
    public async Task CancelWithNothingRunningSaysSo()
    {
        Arrange();

        var (_, renderer) = await Run("wabbajack", "cancel");

        Acquirer.DidNotReceive().Cancel();
        Lines(renderer).Should().ContainSingle().Which.Should().Contain("No modlist download is running");
    }
}
