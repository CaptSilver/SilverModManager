using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Games.CreationEngine.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public sealed class OpenMwLocatorTests : IDisposable
{
    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);
    private readonly TempOpenMwConfigRoots _roots;
    private readonly StubbedMorrowindInstall _install;
    private readonly RecordedProblems _problems = new();

    public OpenMwLocatorTests()
    {
        _roots = new TempOpenMwConfigRoots(_temp);
        _install = new StubbedMorrowindInstall(_temp);
        _install.Write();
    }

    public void Dispose() => _temp.Dispose();

    private sealed class RecordedProblems : IGameLocatorProblemSink
    {
        public List<(string Locator, string Message)> Reports { get; } = [];
        public void Report(string locator, string message) => Reports.Add((locator, message));
    }

    /// <summary>A game that is only an id: the locator needs no more of it.</summary>
    private sealed class FakeOpenMw : IGameData
    {
        public GameId GameId => CreationEngine.OpenMw.OpenMw.GameId;
        public StoreIdentifiers StoreIdentifiers { get; } = new(CreationEngine.OpenMw.OpenMw.GameId);
        public string DisplayName => "OpenMW";
        public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private List<GameLocatorResult> Locate() =>
        new OpenMwLocator(_roots, FileSystem.Shared, [new FakeOpenMw()]).Locate(_problems).ToList();

    [Fact]
    public void OnlyTheFirstRootHoldingAConfigIsFound()
    {
        _roots.WriteConfig(0, _install.Config());

        var found = Locate().Should().ContainSingle().Subject;
        found.Store.Should().Be(GameStore.OpenMW);
        found.StoreIdentifier.Should().Be(_roots.NativeConfig.GetFullPath());
        found.Path.Should().Be(_install.Folder);
    }

    [Fact]
    public void OnlyTheSecondRootHoldingAConfigIsFound()
    {
        _roots.WriteConfig(1, _install.Config());

        var found = Locate().Should().ContainSingle().Subject;
        found.Store.Should().Be(GameStore.OpenMWFlatpak);
        found.StoreIdentifier.Should().Be(_roots.FlatpakConfig.GetFullPath());
    }

    [Fact]
    public void NoConfigAnywhereFindsNothing()
    {
        Locate().Should().BeEmpty();
        _problems.Reports.Should().BeEmpty();
    }

    [Fact]
    public void ARootFolderWithoutAConfigDoesNotCount()
    {
        File.WriteAllText((_roots.NativeConfig / "settings.cfg").GetFullPath(), "x\n");

        Locate().Should().BeEmpty();
        _problems.Reports.Should().BeEmpty();
    }

    [Fact]
    public void TwoRootsGiveTwoInstallations()
    {
        _roots.WriteConfig(0, _install.Config());
        _roots.WriteConfig(1, _install.Config());

        var found = Locate();

        found.Select(result => result.Store).Should().Equal(GameStore.OpenMW, GameStore.OpenMWFlatpak);
        found.Select(result => result.Path).Should().AllBeEquivalentTo(_install.Folder);
        found.Select(result => result.StoreIdentifier).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AMissingProgramIsReportedAndNoRowGiven()
    {
        _roots.WriteConfig(0, _install.Config());
        _roots.NativeProgram = null;

        Locate().Should().BeEmpty();
        _problems.Reports.Should().ContainSingle().Which.Message
            .Should().Be($"openmw.cfg found at {_roots.NativeConfig} but OpenMW is not installed");
    }

    [Fact]
    public void AConfigWithNoMorrowindDataFolderIsReported()
    {
        _roots.WriteConfig(0, "content=builtin.omwscripts\n");

        Locate().Should().BeEmpty();
        _problems.Reports.Should().ContainSingle().Which.Message
            .Should().Contain("installation wizard").And.Contain(_roots.NativeConfig.GetFullPath());
    }

    [Fact]
    public void ADataFolderThatHoldsNoMorrowindEsmDoesNotCount()
    {
        var other = _temp.CreateFolder("mods").Path;
        _roots.WriteConfig(0, $"data=\"{other.GetFullPath()}\"\ndata=\"{_install.DataFiles.GetFullPath()}\"\n");

        Locate().Should().ContainSingle().Which.Path.Should().Be(_install.Folder);
    }

    [Fact]
    public void ADataFolderWithAnEscapedQuoteIsResolvedWithTheLaunchersRules()
    {
        var odd = _temp.CreateFolder("my\"games").Path;
        odd.Combine("Data Files").CreateDirectory();
        File.WriteAllBytes((odd / "Data Files/Morrowind.esm").GetFullPath(), StubbedMorrowindInstall.Plugin(true));
        _roots.WriteConfig(0, $"data=\"{odd.GetFullPath().Replace("\"", "&\"")}/Data Files\"\n");

        Locate().Should().ContainSingle().Which.Path.Should().Be(odd);
    }
}
