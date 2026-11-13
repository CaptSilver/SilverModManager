using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Sekiro;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests;

public class SekiroDefinitionTests
{
    private static FromSoftGameDefinition Definition => SekiroGame.Definition;

    [Fact]
    public void SekiroHasNoOnlinePlayAndNoRegulation()
    {
        Definition.HasOnlinePlay.Should().BeFalse();
        Definition.Formats.RegulationFileName.Should().BeNull();
    }

    [Fact]
    public void TheCleanupVerbIsTheSharedOne()
        => Definition.CleanupVerb.Should().Be("fromsoft cleanup");

    [Fact]
    public void ModEngine1sIniIsAMarkerAndALeftoverExactlyOnce()
    {
        Definition.CheckRules.ModLoaderFiles.Should().ContainSingle(f => f == "modengine.ini");
        Definition.Cleanup.Leftovers.Should().ContainSingle(f => f == "modengine.ini");
    }

    [Fact]
    public void LoaderFilesAtSekirosRootOnlyWarn()
    {
        var root = new InMemoryFileSystem().FromUnsanitizedFullPath("/steam/Sekiro");
        root.CreateDirectory();
        foreach (var name in new[] { "sekiro.exe", "dinput8.dll", "modengine.ini" })
        {
            using var stream = (root / name).Create();
            stream.Write(new byte[1]);
        }

        var results = FromSoftChecks.GameFolder(root, Definition);

        var artifacts = results.Should().ContainSingle(r => r.Id == "artifacts").Subject;
        artifacts.Level.Should().Be(CheckLevel.Warn);
        artifacts.Message.Should().Contain("dinput8.dll").And.Contain("modengine.ini");
        results.Should().NotContain(r => r.Id == "eac");
    }

    [Fact]
    public void SoundIsSkippedAndSdIsNot()
    {
        FromSoftPaths.IsArchiveOrMedia(Definition, new GamePath(LocationId.Game, "sound/x.fsb")).Should().BeTrue();
        FromSoftPaths.IsArchiveOrMedia(Definition, new GamePath(LocationId.Game, "SOUND/x.fsb")).Should().BeTrue();
        FromSoftPaths.IsArchiveOrMedia(Definition, new GamePath(LocationId.Game, "sd/x.fsb")).Should().BeFalse();
    }

    [Fact]
    public void TheRegulationStrategyIsNeverOffered()
    {
        MergeStrategies.ForPath("regulation.bin", Definition.Formats, true).Should().BeNull();
        MergeStrategies.ForPath("param/gameparam/gameparam.parambnd.dcx", Definition.Formats, true).Should().BeNull();
    }

    [Fact]
    public void ThePrimaryFileIsSekiroExeAtTheInstallRoot()
        => FromSoftPaths.InGameFolder(Definition.Layout, Definition.Layout.GameExecutable).Path.Path.Should().Be("sekiro.exe");
}
