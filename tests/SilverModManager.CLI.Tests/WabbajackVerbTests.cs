using FluentAssertions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Cli;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Update;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using Table = SilverModManager.Sdk.ProxyConsole.Table;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod wabbajack inspect</c> over the checked-in real modlist. The verb only reads the file, so the
/// library is never involved.
/// </summary>
public class WabbajackVerbTests(IServiceProvider provider, TemporaryFileManager temporaryFileManager)
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Resources", "MO2AndSKSETest.wabbajack");

    private async Task<(int ExitCode, LoggingRenderer Renderer)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static string[] Lines(LoggingRenderer renderer) => renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments.Cast<object>().ToArray()).TrimEnd('\n')).ToArray();

    private static string[][] Cells(Table table)
        => table.Rows.Select(row => row.Select(c => c is Text text ? text.Template : "").ToArray()).ToArray();

    [Fact]
    public async Task InspectPrintsTheListsHeaderAndCountsPerKind()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Fixture);

        exitCode.Should().Be(0);
        Lines(renderer).Should().Contain(["Name: MO2AndSKSETest", "Version: 1.0.0.0", "GameType: SkyrimSpecialEdition"]);
        Lines(renderer).Should().Contain(l => l.StartsWith("Author:"));

        var tables = renderer.Logs.OfType<Table>().ToArray();
        tables.Should().HaveCount(3);
        Cells(tables[0]).Should().BeEquivalentTo(new[]
        {
            new[] { "FromArchive", "1395" },
            new[] { "InlineFile", "11" },
            new[] { "ArchiveMeta", "2" },
            new[] { "RemappedInlineFile", "1" },
        });
        Cells(tables[1]).Should().BeEquivalentTo(new[] { new[] { "Http", "2" } });
    }

    [Fact]
    public async Task InspectListsEachArchiveWithItsSizeAndSource()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Fixture);

        exitCode.Should().Be(0);
        var archives = Cells(renderer.Logs.OfType<Table>().Last());
        archives.Should().BeEquivalentTo(new[]
        {
            new[] { "Mod.Organizer-2.4.2.7z", "89503525", "Http", "https://github.com/ModOrganizer2/modorganizer/releases/download/v2.4.2/Mod.Organizer-2.4.2.7z" },
            new[] { "skse64_2_00_19.7z", "922047", "Http", "https://skse.silverlock.org/beta/skse64_2_00_19.7z" },
        });
    }

    [Fact]
    public async Task InspectRefusesAFileThatIsNotAModlist()
    {
        await using var folder = temporaryFileManager.CreateFolder();
        var path = folder.Path / "not-a-modlist.wabbajack";
        await using (var stream = File.Create(path.ToString()))
        await using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            await using (var entry = zip.CreateEntry("something-else").Open())
                await entry.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);

        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", path.ToString());

        exitCode.Should().NotBe(0);
        Lines(renderer).Should().ContainSingle(l => l.Contains("no 'modlist' entry"));
    }

    [Fact]
    public async Task InspectFailsOnAMissingFile()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Path.Combine(Fixture, "..", "nope.wabbajack"));

        exitCode.Should().NotBe(0);
        Lines(renderer).Should().ContainSingle(l => l.Contains("does not exist"));
    }

    private static string RawText(LoggingRenderer renderer) => string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));

    [Fact]
    public async Task Inspect_Json_CarriesTheHeaderCountsAndArchives()
    {
        var (exitCode, renderer) = await Run("wabbajack", "inspect", "-f", Fixture, "--json");

        exitCode.Should().Be(0);
        var list = JsonNode.Parse(RawText(renderer))!.AsObject();
        ((string?)list["name"]).Should().Be("MO2AndSKSETest");
        ((string?)list["gameType"]).Should().Be("SkyrimSpecialEdition");
        ((int)list["directiveCount"]!).Should().Be(1409);
        list["directives"]!.AsArray().Select(n => (string?)n!["kind"]).Should().Contain("FromArchive");
        list["archives"]!.AsArray().Select(n => (string?)n!["source"]).Should().OnlyContain(source => source == "Http");
        renderer.Logs.OfType<Table>().Should().BeEmpty("the JSON replaces the tables");
    }
}

/// <summary>
/// <c>silvermod wabbajack updates</c> against an installed modlist and a doubled gallery client: what it lists, and what it
/// says when the listing is an offline copy or unavailable.
/// </summary>
[Collection("ConsoleOutput")]
public class WabbajackUpdatesVerbTests(ITestOutputHelper helper) : WabbajackUpdateVerbTestBase<WabbajackUpdatesVerbTests>(helper)
{
    private readonly IWabbajackGalleryClient _gallery = Substitute.For<IWabbajackGalleryClient>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        return services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackVerbs()
            .AddSingleton(_gallery)
            .AddSingleton<ModlistUpdateCheck>();
    }

    private async Task<(int ExitCode, LoggingRenderer Renderer)> RunUpdates(LoadoutId loadout)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>()
            .RunAsync(["wabbajack", "updates", "-l", loadout.ToString()], renderer, CancellationToken.None);
        return (exitCode, renderer);
    }

    private static string[] Lines(LoggingRenderer renderer)
        => renderer.Logs.OfType<Text>().Select(t => string.Format(t.Template, t.Arguments.Cast<object>().ToArray()).TrimEnd('\n')).ToArray();

    private static string[][] Cells(Table table)
        => table.Rows.Select(row => row.Select(c => c is Text text ? string.Format(text.Template, text.Arguments.Cast<object>().ToArray()) : "").ToArray()).ToArray();

    private static GalleryEntry Entry(string title, string version, string machineUrl) => new()
    {
        Title = title,
        Author = "someone",
        GalleryGame = "eldenring",
        MachineUrl = machineUrl,
        Repository = "wabbajack-tools/mod-lists",
        Version = version,
        ArchiveCount = 40,
        ArchivesSize = 2_000_000_000UL,
    };

    [Fact]
    public async Task UpdatesListsTheNewerEntryWithVersionsAndSize()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.0", "fancy");
        var listing = new GalleryListing([Entry("Fancy List", "1.1", "fancy")], DateTimeOffset.UtcNow, IsStale: false);
        _gallery.Get(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(listing);
        _gallery.GetCached().Returns(listing);

        var (exitCode, renderer) = await RunUpdates(loadout.LoadoutId);

        exitCode.Should().Be(0);
        var row = Cells(renderer.Logs.OfType<Table>().Should().ContainSingle().Subject).Should().ContainSingle().Subject;
        row.Should().Contain("Fancy List").And.Contain("1.0").And.Contain("1.1").And.Contain("40");
        row.Should().Contain(cell => cell.Contains("GB"));
        Lines(renderer).Should().NotContain(l => l.Contains("Offline copy"));
    }

    [Fact]
    public async Task UpdatesSaysOfflineCopyFromWhenTheListingIsStale()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.0", "fancy");
        var fetched = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        _gallery.Get(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new GalleryListing([Entry("Fancy List", "1.1", "fancy")], fetched, IsStale: true));

        var (exitCode, renderer) = await RunUpdates(loadout.LoadoutId);

        exitCode.Should().Be(0);
        Lines(renderer).Should().Contain(l => l.Contains("Offline copy from") && l.Contains("2026-11-01"));
        renderer.Logs.OfType<Table>().Should().ContainSingle();
    }

    [Fact]
    public async Task UpdatesPrintsNothingToDoWhenThereIsNoCache()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.0", "fancy");
        _gallery.Get(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns<GalleryListing>(_ => throw new GalleryUnavailableException("offline"));

        var (exitCode, renderer) = await RunUpdates(loadout.LoadoutId);

        exitCode.Should().Be(0);
        Lines(renderer).Should().ContainSingle(l => l.StartsWith("Nothing to do"));
        renderer.Logs.OfType<Table>().Should().BeEmpty();
    }
}

/// <summary>The verbs that move a list to a newer version, run with a doubled <see cref="ModlistUpdater"/>.</summary>
public abstract class WabbajackUpdaterVerbTestBase<TSelf>(ITestOutputHelper helper) : WabbajackUpdateVerbTestBase<TSelf>(helper)
{
    protected ModlistUpdater Updater => ServiceProvider.GetRequiredService<ModlistUpdater>();

    protected abstract bool WithWindow { get; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackVerbs()
            .AddSingleton<IModlistCatalog, ModlistCatalog>()
            .AddSingleton(provider => Substitute.For<ModlistUpdater>(provider));
        if (WithWindow) services.AddSingleton<WindowHost>();
        return services;
    }

    protected static string[] UpdateArgs(Loadout.ReadOnly loadout, params string[] more)
        => ["wabbajack", "update", "-m", "Fancy List 1.1.wabbajack", "-l", loadout.LoadoutId.ToString(), .. more];
}

/// <summary>
/// <c>silvermod wabbajack update</c> against an installed list and a doubled <see cref="ModlistUpdater"/>: what it prints,
/// refuses and hands to the updater.
/// </summary>
[Collection("ConsoleOutput")]
public class WabbajackUpdateVerbTests(ITestOutputHelper helper) : WabbajackUpdaterVerbTestBase<WabbajackUpdateVerbTests>(helper)
{
    protected override bool WithWindow => true;

    private static ArchivePlanRow Missing(string name)
        => new(new ArchiveEntry(FileIdentity.XxHash64(NexusMods.Hashing.xxHash3.Hash.From((ulong)name.GetHashCode())), 5_000, name, "", new HttpState(new Uri("https://example.test/" + name), [])),
            ArchiveState.Automatic, null, null, null);

    private static ModlistUpdatePreviewResult Review(IReadOnlyList<RelativePath>? gameFilesKept = null) => new(
        [Missing("big.7z")], Size.From(5_000), Size.From(9_000),
        Added: ["Brand New"], Changed: ["Core", "Textures"], Renamed: [("Old Name", "New Name")], Removed: [],
        RetriedTextures: [], GameFilesKept: gameFilesKept ?? [], UserTweaksKept: [], Warnings: ["Heads up"],
        ModOrder: [], Plugins: ["Mine.esp"], Conflicts: ["Core"]);

    /// <summary>The transaction that wrote the install, which changed the loadout and so is one of its revisions.</summary>
    private TxId InstallTx { get; set; }

    private async Task<Loadout.ReadOnly> InstalledOneAndNewOne(IReadOnlyList<RelativePath>? gameFilesKept = null)
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.0", "fancy");
        InstallTx = Connection.Db.BasisTxId;
        await AddListFile("Fancy List", "1.1", "fancy");
        Updater.Review(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<CancellationToken>())
            .Returns(Review(gameFilesKept));
        return loadout;
    }

    [Fact]
    public async Task UpdateWithoutYesPrintsTheReviewAndChangesNothing()
    {
        var loadout = await InstalledOneAndNewOne();
        var before = Connection.Db.BasisTxId;

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout));

        exitCode.Should().Be(0);
        var lines = LinesOf(renderer);
        lines.Should().Contain(l => l.StartsWith("A newer version of Fancy List is available (1.0 → 1.1, 1 archives to download, "));
        lines.Should().Contain(["Mods added: 1", "Mods changed: 2", "Mods renamed: 1", "Mods removed: 0", "Your edits the list overrides — kept yours: 2", "Heads up"]);
        lines.Should().Contain("A restore point is written first; Undo is on the modlist page.");
        lines.Should().NotContain("  Brand New", "the items are listed only with --review");
        await Updater.DidNotReceiveWithAnyArgs().Apply(default, default!, default, default!, default);
        await Updater.DidNotReceiveWithAnyArgs().Download(default!, default, default);
        Connection.Db.BasisTxId.Should().Be(before, "a review writes nothing");
    }

    [Fact]
    public async Task UpdateWithReviewListsWhatEachCountCounts()
    {
        var loadout = await InstalledOneAndNewOne();

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout, "--review"));

        exitCode.Should().Be(0);
        LinesOf(renderer).Should().Contain(["  Brand New", "  Core", "  Textures", "  Old Name → New Name", "  Mine.esp", "Archives to download:", "  big.7z"]);
    }

    private void ApplyReturnsTheGroupWithRestorePoint(TxId restorePoint)
    {
        Updater.Apply(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<RelativePath>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<WabbajackModlistLoadoutGroup.ReadOnly>()));
        Updater.RestorePointOf(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>()).Returns(restorePoint);
    }

    [Fact]
    public async Task UpdateWithYesDownloadsWhatIsMissingThenAppliesAndNamesTheRestorePoint()
    {
        var loadout = await InstalledOneAndNewOne([(RelativePath)"Game/keep.ini"]);
        Updater.IsDownloaded(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>()).Returns(false);
        ApplyReturnsTheGroupWithRestorePoint(InstallTx);

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout, "--yes", "--skip-missing"));

        exitCode.Should().Be(0);
        Received.InOrder(() =>
        {
            Updater.EnsureCanApply(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>());
            Updater.Download(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), loadout.LoadoutId, Arg.Any<CancellationToken>());
            Updater.Apply(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), true,
                Arg.Is<IReadOnlySet<RelativePath>>(kept => kept.SetEquals(new[] { (RelativePath)"Game/keep.ini" })), Arg.Any<CancellationToken>());
        });
        var lines = LinesOf(renderer);
        lines.Should().Contain("Updated Fancy List from 1.0 to 1.1.");
        lines.Should().Contain("To go back to 1.0: " + UndoCommandFor(loadout));
        lines.Should().NotContain(l => l.Contains("loadout revert"), "loadout revert leaves the update's protection behind");
    }

    private static string UndoCommandFor(Loadout.ReadOnly loadout) => $"wabbajack undo-update -m \"Fancy List 1.1.wabbajack\" -l {loadout.LoadoutId}";

    [Fact]
    public async Task UpdateWithYesNamesTheRevisionLoadoutRevisionsListsForTheRestorePoint()
    {
        var loadout = await InstalledOneAndNewOne();
        await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        var restorePoint = await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        await LoadoutSnapshot.WriteRestorePoint(Connection, loadout.LoadoutId);
        Updater.IsDownloaded(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>()).Returns(true);
        var (_, revisions) = await RunVerb("loadout", "revisions", "-l", loadout.LoadoutId.ToString(), "--json");
        var listed = JsonNode.Parse(RawText(revisions))!.AsArray();
        var expected = listed.Single(row => row!["tx"]!.GetValue<string>() == EntityId.From(restorePoint.Value).ToString())!["number"]!.GetValue<int>();
        expected.Should().Be(listed.Count - 2, "the restore point is the second newest revision");
        ApplyReturnsTheGroupWithRestorePoint(restorePoint);

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout, "--yes"));

        exitCode.Should().Be(0);
        LinesOf(renderer).Should().Contain($"restore point: revision {expected} in 'loadout revisions'");
    }

    [Fact]
    public async Task UpdateWithYesNamesTheTransactionWhenNoRevisionPrecedesTheRestorePoint()
    {
        var loadout = await InstalledOneAndNewOne();
        Updater.IsDownloaded(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>()).Returns(true);
        ApplyReturnsTheGroupWithRestorePoint(TxId.From(1));

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout, "--yes", "--json"));

        exitCode.Should().Be(0);
        var result = JsonNode.Parse(RawText(renderer))!;
        result["restorePointRevision"].Should().BeNull();
        result["restorePointTransaction"]!.GetValue<ulong>().Should().Be(1);
        result["undoVerb"]!.GetValue<string>().Should().Be(UndoCommandFor(loadout));

        var (_, text) = await RunVerb(UpdateArgs(loadout, "--yes"));
        LinesOf(text).Should().Contain("restore point: transaction 1");
    }

    [Fact]
    public async Task UpdateWithYesSkipsTheDownloadWhenTheLibraryHasEveryArchive()
    {
        var loadout = await InstalledOneAndNewOne();
        Updater.IsDownloaded(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>()).Returns(true);
        ApplyReturnsTheGroupWithRestorePoint(InstallTx);

        var (exitCode, _) = await RunVerb(UpdateArgs(loadout, "--yes"));

        exitCode.Should().Be(0);
        await Updater.DidNotReceiveWithAnyArgs().Download(default!, default, default);
        await Updater.ReceivedWithAnyArgs(1).Apply(default, default!, default, default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRefusedByTheChecksBeforeApplyDownloadsNothingAndDoesNotOfferYes(bool yes)
    {
        var loadout = await InstalledOneAndNewOne();
        var refusal = new ModlistUpdateRefusedException("Fancy List 1.1 has been taken down from the gallery, so it is not updated.");
        Updater.When(u => u.EnsureCanApply(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>())).Throw(refusal);

        var (exitCode, renderer) = await RunVerb(yes ? UpdateArgs(loadout, "--yes") : UpdateArgs(loadout));

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().Be(CliErrors.Describe(refusal));
        await Updater.DidNotReceiveWithAnyArgs().Download(default!, default, default);
        await Updater.DidNotReceiveWithAnyArgs().Apply(default, default!, default, default!, default);
    }

    [Fact]
    public async Task UpdateRefusesTheSupersededPreviousFileAsADowngrade()
    {
        var loadout = await CreateLoadout();
        var old = await AddListFile("Fancy List", "1.0", "fancy");
        var group = await InstallGroup(loadout.LoadoutId, await AddListFile("Fancy List", "1.1", "fancy"), "Fancy List");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group, WabbajackModlistLoadoutGroup.PreviousModlist, old);
            await tx.Commit();
        }

        var (exitCode, renderer) = await RunVerb("wabbajack", "update", "-m", "Fancy List 1.0.wabbajack", "-l", loadout.LoadoutId.ToString(), "--yes");

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().Be(CliErrors.Describe(new ModlistUpdateRefusedException(
            "Fancy List 1.0 is not newer than the installed 1.1, and an update only moves forward. 'wabbajack undo-update' goes back to the version before the last update.")));
        await Updater.DidNotReceiveWithAnyArgs().Review(default, default!, default);
    }

    [Fact]
    public async Task UpdateRefusesAnOlderReleaseOfTheInstalledListAsADowngrade()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.1", "fancy");
        await AddListFile("Fancy List", "1.0", "fancy");

        var (exitCode, renderer) = await RunVerb("wabbajack", "update", "-m", "Fancy List 1.0.wabbajack", "-l", loadout.LoadoutId.ToString());

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().Contain("is not newer than the installed 1.1");
        await Updater.DidNotReceiveWithAnyArgs().Review(default, default!, default);
    }

    [Fact]
    public async Task UpdateRefusalsPrintThroughCliErrors()
    {
        var loadout = await InstalledOneAndNewOne();
        var refusal = new ModlistUpdateRefusedException("1 archives are still missing: `big.7z`; nothing was changed");
        Updater.IsDownloaded(Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>()).Returns(true);
        Updater.Apply(Arg.Any<WabbajackModlistLoadoutGroup.ReadOnly>(), Arg.Any<WabbajackModlistLibraryFile.ReadOnly>(), Arg.Any<bool>(), Arg.Any<IReadOnlySet<RelativePath>>(), Arg.Any<CancellationToken>())
            .Returns<Task<WabbajackModlistLoadoutGroup.ReadOnly>>(_ => throw refusal);
        await AddListFile("Other List", "2.0", "other");

        var (refusedExit, refused) = await RunVerb(UpdateArgs(loadout, "--yes"));
        var (unmatchedExit, unmatched) = await RunVerb("wabbajack", "update", "-m", "Other List 2.0.wabbajack", "-l", loadout.LoadoutId.ToString());

        refusedExit.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(refused).Should().EndWith(CliErrors.Describe(refusal));
        LinesOf(refused).Should().NotContain(l => l.Contains('`') || l.Contains("   at "), "a refusal is plain text, never a stack trace");
        unmatchedExit.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(unmatched).Should().ContainSingle().Which.Should().Be(
            CliErrors.Describe(new ModlistUpdateRefusedException($"No modlist in {loadout.Name} is an earlier version of Other List 2.0. Name the installed list with --from.")));
    }
}

/// <summary><c>wabbajack update --yes</c> in a headless main, which has no window to run the downloads and the install in.</summary>
[Collection("ConsoleOutput")]
public class WabbajackUpdateWithoutWindowVerbTests(ITestOutputHelper helper) : WabbajackUpdaterVerbTestBase<WabbajackUpdateWithoutWindowVerbTests>(helper)
{
    protected override bool WithWindow => false;

    [Fact]
    public async Task UpdateWithYesAndNoWindowRefusesReadably()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.0", "fancy");
        await AddListFile("Fancy List", "1.1", "fancy");

        var (exitCode, renderer) = await RunVerb(UpdateArgs(loadout, "--yes"));

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().StartWith("Updating a modlist needs the SilverModManager window to be open");
        await Updater.DidNotReceiveWithAnyArgs().Review(default, default!, default);
        await Updater.DidNotReceiveWithAnyArgs().Apply(default, default!, default, default!, default);
    }
}

/// <summary><c>wabbajack finish-update</c> on a list that was updated away from an old version holding an archive only it used.</summary>
[Collection("ConsoleOutput")]
public class WabbajackFinishUpdateVerbTests(ITestOutputHelper helper) : WabbajackUpdateVerbTestBase<WabbajackFinishUpdateVerbTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        return services
            .AddWabbajackModlistLibraryFileModel()
            .AddWabbajackModlistLoadoutGroupModel()
            .AddWabbajackModlistArchiveModel()
            .AddWabbajackVerbs()
            .AddSingleton<IModlistCatalog, ModlistCatalog>()
            .AddSingleton<ModlistInstallRuns>()
            .AddSingleton<ModlistRemovalPlanner>()
            .AddSingleton<ModlistUpdateFinish>()
            .AddSingleton(provider => new ModlistUpdater(provider));
    }

    private async Task<(Loadout.ReadOnly Loadout, EntityId Old, EntityId Group)> UpdatedAwayFromAnOldVersion()
    {
        var loadout = await CreateLoadout();
        var old = await AddListFile("Fancy List", "1.0", "fancy");
        var current = await AddListFile("Fancy List", "1.1", "fancy");
        var group = await InstallGroup(loadout.LoadoutId, current, "Fancy List");

        using var tx = Connection.BeginTransaction();
        _ = new LibraryFile.New(tx, out var archiveId)
        {
            FileName = "old-only.zip",
            Hash = NexusMods.Hashing.xxHash3.Hash.From(77UL),
            Size = Size.From(10),
            LibraryItem = new LibraryItem.New(tx, archiveId) { Name = "old-only.zip" },
        };
        var row = new WabbajackModlistArchive.New(tx)
        {
            ModlistId = old,
            Identity = FileIdentity.XxHash64(NexusMods.Hashing.xxHash3.Hash.From(77UL)),
            Size = Size.From(10),
            Name = "old-only.zip",
        };
        tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, archiveId);
        var restorePoint = EntityId.From(Connection.Db.BasisTxId.Value);
        tx.Add(group, WabbajackModlistLoadoutGroup.PreviousModlist, old);
        tx.Add(group, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, restorePoint);
        tx.Add(group, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        _ = new ProtectedLoadoutRevision.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            RevisionId = TransactionId.From(restorePoint),
            Reason = "Wabbajack update of Fancy List",
        };
        await tx.Commit();
        return (loadout, old, group);
    }

    [Fact]
    public async Task FinishUpdateReleasesTheRestorePointAndListsWhatItRemoved()
    {
        var (loadout, old, group) = await UpdatedAwayFromAnOldVersion();

        var (exitCode, renderer) = await RunVerb("wabbajack", "finish-update", "-m", "Fancy List 1.1.wabbajack", "-l", loadout.LoadoutId.ToString(), "--delete-old-archives");

        exitCode.Should().Be(0);
        LinesOf(renderer).Should().Contain([
            "Finished the update of Fancy List: the restore point is released, so the update can no longer be undone.",
            "Removed the old version Fancy List from the library.",
            "Removed archive old-only.zip",
        ]);
        var released = WabbajackModlistLoadoutGroup.Load(Connection.Db, group);
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(released, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(released, out _).Should().BeFalse();
        ProtectedLoadoutRevision.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        WabbajackModlistLibraryFile.Load(Connection.Db, old).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task FinishUpdateWithoutDeletingKeepsTheOldVersionAndSaysHowToRemoveIt()
    {
        var (loadout, old, _) = await UpdatedAwayFromAnOldVersion();

        var (exitCode, renderer) = await RunVerb("wabbajack", "finish-update", "-m", "Fancy List 1.1.wabbajack", "-l", loadout.LoadoutId.ToString());

        exitCode.Should().Be(0);
        LinesOf(renderer).Should().Contain(l => l.StartsWith("The old version Fancy List stays in the library.") && l.Contains("--delete-old-archives") && l.Contains("old-only.zip"));
        WabbajackModlistLibraryFile.Load(Connection.Db, old).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task FinishUpdateRefusesAListThatWasNotUpdated()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.1", "fancy");

        var (exitCode, renderer) = await RunVerb("wabbajack", "finish-update", "-m", "Fancy List", "-l", loadout.LoadoutId.ToString());

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().Be($"Fancy List has no update to finish in {loadout.Name}.");
    }

    [Fact]
    public async Task UndoUpdateRestoresTheGroupAndReleasesTheProtectionRow()
    {
        var (loadout, _, group) = await UpdatedAwayFromAnOldVersion();
        ProtectedLoadoutRevision.All(Connection.Db).Should().ContainSingle();

        var (exitCode, renderer) = await RunVerb("wabbajack", "undo-update", "-m", "Fancy List 1.1.wabbajack", "-l", loadout.LoadoutId.ToString());

        exitCode.Should().Be(0, string.Join("\n", LinesOf(renderer)));
        LinesOf(renderer).Should().ContainSingle().Which.Should().Be("Undid the update of Fancy List: it is back on 1.0.");
        var restored = WabbajackModlistLoadoutGroup.Load(Connection.Db, group);
        WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(restored, out _).Should().BeFalse();
        WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(restored, out _).Should().BeFalse();
        ProtectedLoadoutRevision.All(Connection.Db).Should().BeEmpty("a revert alone leaves the protection row behind, and nothing could release it afterwards");
    }

    [Fact]
    public async Task UndoUpdateRefusesAListThatWasNotUpdated()
    {
        var loadout = await CreateLoadout();
        await InstallList(loadout.LoadoutId, "Fancy List", "1.1", "fancy");

        var (exitCode, renderer) = await RunVerb("wabbajack", "undo-update", "-m", "Fancy List 1.1.wabbajack", "-l", loadout.LoadoutId.ToString());

        exitCode.Should().Be(CliErrors.RefusalExitCode);
        LinesOf(renderer).Should().ContainSingle().Which.Should().Be(CliErrors.Describe(new ModlistUpdateRefusedException("Fancy List has no update to undo.")));
    }
}
