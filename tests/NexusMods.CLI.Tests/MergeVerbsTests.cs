using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.FromSoft.Tests.Formats.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.CLI.Tests;

/// <summary>
/// <c>silvermod fromsoft merge list</c> over loadouts whose mods overlap on a message archive or a regulation. The
/// report only reads, so the data root must look the same after every run.
/// </summary>
[Collection("ConsoleOutput")]
public class MergeVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<MergeVerbsTests, EldenRingGame>(helper)
{
    private const string StubbedSteamId = "3976631895";
    private const string Msg = "msg/engus/item.msgbnd.dcx";
    private const string RegulationPath = "regulation.bin";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"MergeVerbs.{Guid.NewGuid():N}");
        base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From(StubbedSteamId)],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        return services;
    }

    private AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    private string[] DataRootListing()
        => DataRoot.DirectoryExists()
            ? Directory.EnumerateFileSystemEntries(DataRoot.ToString(), "*", SearchOption.AllDirectories).Order().ToArray()
            : [];

    private static byte[] Msgbnd(string text)
        => MsgbndFixture.Build(new Dictionary<int, IReadOnlyDictionary<int, string?>>
        {
            [1] = new Dictionary<int, string?> { [10] = text },
        }, Formats);

    private static byte[] Regulation(string version, byte fill)
        => RegulationFixture.BuildFromEntries([(1, "a.param", Enumerable.Repeat(fill, 16).ToArray())], version, Formats);

    private async Task InstallPackage(Loadout.ReadOnly loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
    }

    private async Task<(int ExitCode, LoggingRenderer Renderer, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, renderer, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    private async Task<Loadout.ReadOnly> MergingPair()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Alpha", (Msg, Msgbnd("alpha text")));
        await InstallPackage(loadout, "Beta", (Msg, Msgbnd("beta text")));
        return Refresh(loadout);
    }

    [Fact]
    public async Task AMergingPairListsOneRowWithTheStrategyAndBothMods()
    {
        var loadout = await MergingPair();
        var before = DataRootListing();

        var (exitCode, renderer, output) = await Run("fromsoft", "merge", "list", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        renderer.LastTableColumns.Should().Equal("Path", "Strategy", "Outcome", "Contributors", "Vanilla source", "Reason");
        var row = renderer.LastCellsAsStrings().Should().ContainSingle().Which.ToArray();
        row[0].Should().Be(Msg);
        row[1].Should().Be("fmg-union");
        row[2].Should().Be("Merged");
        row[3].Should().Contain("Alpha.zip").And.Contain("Beta.zip");
        output.Should().Contain("both mods ship it and differ");
        output.TrimEnd().Split('\n').Last().Should().Be(MergeReport.DefaultPriorityOrderHint);
        DataRootListing().Should().Equal(before);
    }

    [Fact]
    public async Task JsonCarriesTheSameEntriesAsTheReport()
    {
        var loadout = await MergingPair();
        var expected = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);
        var before = DataRootListing();

        var (exitCode, _, output) = await Run("fromsoft", "merge", "list", "--json", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        var doc = JsonDocument.Parse(output);
        doc.RootElement.GetProperty("refusal").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("priorityOrderHint").GetString().Should().Be(expected.PriorityOrderHint);
        var entry = doc.RootElement.GetProperty("entries").EnumerateArray().Should().ContainSingle().Which;
        entry.GetProperty("path").GetString().Should().Be(Msg);
        entry.GetProperty("strategy").GetString().Should().Be("fmg-union");
        entry.GetProperty("outcome").GetString().Should().Be("Merged");
        entry.GetProperty("contributors").EnumerateArray().Select(e => e.GetString()).Should().Equal(expected.Entries.Single().Contributors);
        entry.GetProperty("notes").EnumerateArray().Select(e => e.GetString()).Should().Equal(expected.Entries.Single().Notes);
        entry.GetProperty("vanillaSource").GetString().Should().Be(expected.Entries.Single().VanillaSource);
        entry.GetProperty("reason").GetString().Should().Be(expected.Entries.Single().Reason);
        JsonSerializer.Deserialize<MergeReport>(output, ReadOptions)!.Entries.Should().HaveCount(1);
        DataRootListing().Should().Equal(before);
    }

    [Fact]
    public async Task ARefusedPairExitsNonZeroAndPrintsTheRefusalFirst()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Alpha", (RegulationPath, Regulation("11701000", 0x10)));
        await InstallPackage(loadout, "Beta", (RegulationPath, Regulation("11601000", 0x20)));
        loadout = Refresh(loadout);
        var expected = await ServiceProvider.GetRequiredService<IMergeReports>().ForLoadout(loadout.LoadoutId, CancellationToken.None);
        var before = DataRootListing();

        var (exitCode, renderer, output) = await Run("fromsoft", "merge", "list", "-l", loadout.Name);

        expected.Refusal.Should().NotBeNullOrEmpty();
        exitCode.Should().NotBe(0);
        renderer.Logs.OfType<Text>().First().Template.Should().StartWith(expected.Refusal);
        renderer.LastCellsAsStrings().Should().ContainSingle().Which.Should().Contain("Refused");
        DataRootListing().Should().Equal(before);
    }

    [Fact]
    public async Task AnEmptyLoadoutPrintsTheEmptyMessageAndExitsZero()
    {
        var loadout = await CreateLoadout();
        var before = DataRootListing();

        var (exitCode, renderer, output) = await Run("fromsoft", "merge", "list", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        output.Should().Contain("No path is provided by more than one mod");
        renderer.Logs.OfType<Table>().Should().BeEmpty();
        DataRootListing().Should().Equal(before);
    }

    [Fact]
    public async Task TheGroupsAndTheListVerbResolveAndNoWritingVerbExists()
    {
        var (listExit, _, listOutput) = await Run("fromsoft", "merge", "--help");
        var (groupExit, _, groupOutput) = await Run("fromsoft", "--help");
        var loadout = await CreateLoadout();

        var (addExit, _, _) = await Run("fromsoft", "merge", "add", "-l", loadout.Name);
        var (removeExit, _, _) = await Run("fromsoft", "merge", "remove", "-l", loadout.Name);

        listExit.Should().Be(0);
        groupExit.Should().Be(0);
        listOutput.Should().MatchRegex(@"(?m)^\s+list\s").And.NotMatchRegex(@"(?m)^\s+(add|remove)\s");
        groupOutput.Should().MatchRegex(@"(?m)^\s+merge\s");
        addExit.Should().NotBe(0);
        removeExit.Should().NotBe(0);
    }
}
