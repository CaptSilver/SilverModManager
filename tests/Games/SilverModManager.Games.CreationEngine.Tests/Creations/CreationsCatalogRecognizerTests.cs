using System.Collections.Immutable;
using System.Text;
using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Creations;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.Tests.Creations;

public class CreationsCatalogRecognizerTests : IDisposable
{
    private static readonly GamePath Catalog = new(LocationId.AppData, "ContentCatalog.txt");
    private static readonly GamePath Manifests = new(LocationId.Game, "Creations");
    private static readonly GamePath Data = new(LocationId.Game, "Data");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "recognizer-" + Guid.NewGuid().ToString("N"));
    private readonly AbsolutePath _game;
    private readonly AbsolutePath _appData;
    private readonly GameInstallation _installation;
    private readonly Dictionary<GamePath, SyncNodePart> _disk = new();
    private readonly CreationsCatalogRecognizer _recognizer = new(Catalog, Manifests, Data, "SkyrimCreation");

    public CreationsCatalogRecognizerTests()
    {
        _game = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "game"));
        _appData = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "appdata"));
        Directory.CreateDirectory(_game.ToString());
        Directory.CreateDirectory(_appData.ToString());
        var locations = GameLocations.Create(new Dictionary<LocationId, AbsolutePath>
        {
            [LocationId.Game] = _game,
            [LocationId.AppData] = _appData,
        }.ToImmutableDictionary());
        var locatorResult = new GameLocatorResult
        {
            Game = null!,
            Path = _game,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = null!,
        };
        _installation = new GameInstallation(locatorResult, locations);
    }

    private static string CatalogJson(params (string Key, string Title, string[] Files)[] entries)
    {
        var body = string.Join(",", entries.Select(e =>
            $"\"{e.Key}\": {{ \"Files\": [{string.Join(",", e.Files.Select(f => $"\"{f}\""))}], \"Title\": \"{e.Title}\", \"Version\": \"1.0\" }}"));
        return "{ \"ContentCatalog\": { \"Description\": \"header\" }" + (body.Length > 0 ? "," + body : "") + " }";
    }

    private void WriteCatalog(string json)
    {
        File.WriteAllText(_appData.Combine("ContentCatalog.txt").ToString(), json);
    }

    private void WriteManifest(string name, params string[] entries)
    {
        WriteInCreations(name, CreationsManifestReaderTests.Build(entries));
    }

    private void WriteInCreations(string name, byte[] bytes)
    {
        var path = _game.Combine("Creations").Combine(name).ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void OnDisk(params string[] dataFiles)
    {
        foreach (var file in dataFiles) _disk[Data / file] = new SyncNodePart { Hash = Hash.From(1), Size = Size.From(1), LastModifiedTicks = 0 };
    }

    private Task<GameInstalledRecognition> Recognize() =>
        _recognizer.Recognize(_installation, _disk, CancellationToken.None).AsTask();

    [Fact]
    public void RecordPathsAreTheCatalogAndTheManifestFolder()
    {
        _recognizer.RecordPaths.Should().Equal(Catalog, Manifests);
    }

    [Fact]
    public async Task ACatalogEntryBecomesAnItemWithItsFilesOnDisk()
    {
        WriteCatalog(CatalogJson(("CSV2_1", "Invented Garden", ["ccinvented001-garden.esm", "ccinvented001-garden.bsa"])));
        OnDisk("ccInvented001-Garden.esm", "ccInvented001-Garden.bsa", "unrelated.esp");

        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        var item = result.Items.Should().ContainSingle().Subject;
        item.Source.Should().Be("SkyrimCreation");
        item.SourceId.Should().Be("CSV2_1");
        item.Title.Should().Be("Invented Garden");
        item.Version.Should().Be("1.0");
        item.Confidence.Should().Be(GameInstalledConfidence.GameRecord);
        item.CanBeClaimed.Should().BeFalse();
        item.Extra.Should().BeNull();
        item.Files.Should().BeEquivalentTo([Data / "ccInvented001-Garden.esm", Data / "ccInvented001-Garden.bsa"]);
    }

    [Fact]
    public async Task WhenOneOfTwoFilesIsMissingTheOtherKeepsTheItem()
    {
        WriteCatalog(CatalogJson(("CSV2_1", "Invented Garden", ["a.esm", "a.bsa"])));
        OnDisk("a.bsa");

        var item = (await Recognize()).Items.Should().ContainSingle().Subject;

        item.Files.Should().Equal(Data / "a.bsa");
    }

    [Fact]
    public async Task AnEntryWithNoFileOnDiskIsNotAnItem()
    {
        WriteCatalog(CatalogJson(("CSV2_1", "Invented Garden", ["a.esm"])));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AManifestCoveredByTheCatalogAddsNothing()
    {
        WriteCatalog(CatalogJson(("CSV2_1", "Invented Garden", ["a.esm", "a.bsa"])));
        WriteManifest("ccinvented001-1700000000.manifest", "data/a.esm", "data/A.bsa");
        OnDisk("a.esm", "a.bsa");

        (await Recognize()).Items.Should().ContainSingle().Which.SourceId.Should().Be("CSV2_1");
    }

    [Fact]
    public async Task AManifestTheCatalogDoesNotCoverBecomesAnItemTitledByItsFirstPlugin()
    {
        WriteCatalog(CatalogJson());
        WriteManifest("inventedquest-1700000000.manifest", "data/inventedquest.bsa", "data/InventedQuest.esp");
        OnDisk("inventedquest.bsa", "inventedquest.esp");

        var item = (await Recognize()).Items.Should().ContainSingle().Subject;

        item.SourceId.Should().Be("inventedquest");
        item.Title.Should().Be("InventedQuest");
        item.Version.Should().BeNull();
        item.Confidence.Should().Be(GameInstalledConfidence.GameRecord);
        item.CanBeClaimed.Should().BeFalse();
        item.Files.Should().BeEquivalentTo([Data / "inventedquest.bsa", Data / "inventedquest.esp"]);
    }

    [Fact]
    public async Task AManifestWithNoFileOnDiskIsNotAnItem()
    {
        WriteManifest("inventedquest-1.manifest", "data/inventedquest.esp");

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ManifestsWorkWithoutACatalog()
    {
        WriteManifest("inventedquest-1.manifest", "data/inventedquest.esp");
        OnDisk("inventedquest.esp");

        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        result.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task ACkmFileInTheManifestFolderIsIgnored()
    {
        WriteInCreations("uploaded.ckm", Encoding.ASCII.GetBytes("BTAR not a manifest"));

        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingCatalogIsEmptyAndReadable()
    {
        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ACatalogThatFailsToParseIsUnreadable()
    {
        WriteCatalog("{ \"CSV2_1\": { \"Files\": [");
        OnDisk("a.esm");

        var result = await Recognize();

        result.RecordsReadable.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ATruncatedManifestIsUnreadable()
    {
        WriteCatalog(CatalogJson());
        WriteInCreations("cut-1.manifest", CreationsManifestReaderTests.Build("data/cut.esp")[..^2]);

        (await Recognize()).RecordsReadable.Should().BeFalse();
    }
}
