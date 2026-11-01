using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// The user points the app at a folder or a program and the detector says which catalogue tools it holds.
/// </summary>
public class KnownToolDetectorTests
{
    private static readonly GameId AnyGameId = GameId.From("Tests.Detector");

    private readonly InMemoryFileSystem _fileSystem = new(OSInformation.FakeUnix);
    private readonly IGame _game = Substitute.For<IGame>();

    private static ToolDefinition Definition(string id, string display, params string[] exeNames) => new()
    {
        Id = new ToolId(id),
        DisplayName = display,
        AppliesTo = _ => true,
        ExecutableNames = _ => exeNames,
        Arguments = ArgumentTemplate.Empty,
    };

    private static readonly ToolDefinition TexGen = Definition("texgen", "TexGen", "TexGen.exe", "TexGenx64.exe");
    private static readonly ToolDefinition DynDolod = Definition("dyndolod", "DynDOLOD", "DynDOLOD.exe", "DynDOLODx64.exe");

    private AbsolutePath Folder(params string[] files)
    {
        var folder = _fileSystem.FromUnsanitizedFullPath("/tools/DynDOLOD");
        foreach (var file in files)
        {
            var path = folder.Combine(file);
            path.Parent.CreateDirectory();
            path.Create().Dispose();
        }

        return folder;
    }

    [Fact]
    public void Scan_OfOneFolderFindsEachToolItHolds()
    {
        var folder = Folder("TexGenx64.exe", "DynDOLODx64.exe", "readme.txt");

        var found = new KnownToolDetector([TexGen, DynDolod]).Scan(_game, folder);

        found.Select(f => (f.Definition.Id, f.Installed.Exe.FileName)).Should().Equal(
            (new ToolId("dyndolod"), "DynDOLODx64.exe"),
            (new ToolId("texgen"), "TexGenx64.exe"));
    }

    [Fact]
    public void Scan_MatchesFileNamesWithoutRegardToCase()
    {
        var folder = Folder("dyndolodx64.EXE");

        new KnownToolDetector([DynDolod]).Scan(_game, folder).Should().ContainSingle()
            .Which.Installed.Exe.Should().Be(folder.Combine("dyndolodx64.EXE"));
    }

    [Fact]
    public void Scan_SkipsToolsThatDoNotApplyToTheGame()
    {
        var folder = Folder("TexGenx64.exe");
        var otherGameOnly = TexGen with { AppliesTo = _ => false };

        new KnownToolDetector([otherGameOnly]).Scan(_game, folder).Should().BeEmpty();
    }

    [Fact]
    public void Scan_IgnoresAdHocDefinitionsWhichHaveNoCatalogueId()
    {
        var folder = Folder("mytool.exe");

        new KnownToolDetector([ToolDefinition.AdHoc(AnyGameId, "mine", "mytool.exe")]).Scan(_game, folder).Should().BeEmpty();
    }

    [Fact]
    public void Scan_OfAFolderThatDoesNotExistFindsNothing()
    {
        var missing = _fileSystem.FromUnsanitizedFullPath("/nowhere");

        new KnownToolDetector([TexGen]).Scan(_game, missing).Should().BeEmpty();
    }

    [Fact]
    public void Match_OfOnePickedProgramFindsItsDefinition()
    {
        var folder = Folder("TexGenx64.exe", "DynDOLODx64.exe");

        var found = new KnownToolDetector([TexGen, DynDolod]).Match(_game, folder.Combine("DynDOLODx64.exe"));

        found.Should().ContainSingle().Which.Definition.Id.Should().Be(new ToolId("dyndolod"));
    }

    [Fact]
    public void Match_OfAProgramNoDefinitionKnowsIsEmptySoTheCallerFallsBackToAdHoc()
    {
        var folder = Folder("whatever.exe");

        new KnownToolDetector([TexGen, DynDolod]).Match(_game, folder.Combine("whatever.exe")).Should().BeEmpty();
    }
}
