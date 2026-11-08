using FluentAssertions;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.Wabbajack.Cli;
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
public class WabbajackUpdatesVerbTests(ITestOutputHelper helper) : EldenRingVerbTest<WabbajackUpdatesVerbTests>(helper)
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

    private async Task InstallList(LoadoutId loadout, string name, string version, string machineUrl)
    {
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = NexusMods.Hashing.xxHash3.Hash.From(1UL),
            Size = Size.From(1),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Name = name,
            Author = "someone",
            Version = version,
            GameType = "EldenRing",
            ArchiveCount = 1,
            ArchiveSize = Size.From(1),
            InstallSize = Size.From(1),
            IsNsfw = false,
        };
        tx.Add(id, WabbajackModlistLibraryFile.MachineUrl, machineUrl);
        tx.Add(id, WabbajackModlistLibraryFile.Repository, "wabbajack-tools/mod-lists");
        _ = new WabbajackModlistLoadoutGroup.New(tx, out var groupId)
        {
            ModlistId = id,
            CollectionGroup = new CollectionGroup.New(tx, groupId)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, groupId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = name, LoadoutId = loadout },
                },
            },
        };
        await tx.Commit();
    }

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
