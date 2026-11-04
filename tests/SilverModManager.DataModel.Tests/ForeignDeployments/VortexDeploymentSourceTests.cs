using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.DataModel.ForeignDeployments;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests.ForeignDeployments;

public sealed class VortexDeploymentSourceTests : IDisposable
{
    private readonly string _outside = TestScratch.Combine("vortex-outside-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly VortexDeploymentSource _source = new(NullLogger<VortexDeploymentSource>.Instance);

    public VortexDeploymentSourceTests()
    {
        // A game folder that sits inside a bigger scratch folder, so an escape has somewhere real to land.
        _root = Path.Combine(_outside, "Hogwarts Legacy");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_outside, recursive: true);

    private AbsolutePath Root => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    private string Put(string relative, string content = "x")
    {
        var full = Path.Combine(_root, relative.Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private void Manifest(string name, string targetPath, params (string RelPath, string Source, string Target)[] files)
    {
        var json = JsonSerializer.Serialize(new
        {
            instance = "default",
            version = 1,
            deploymentMethod = "hardlink_activator",
            gameId = "hogwartslegacy",
            deploymentTime = 1700000000,
            stagingPath = @"Z:\staging",
            targetPath,
            files = files.Select(f => new { relPath = f.RelPath, source = f.Source, target = f.Target, time = 1700000000 }).ToArray(),
        });
        File.WriteAllText(Path.Combine(_root, name), json);
    }

    private static GamePath Game(string path) => new(LocationId.Game, path);

    [Fact]
    public void Detect_WithNoManifest_FindsNothing()
    {
        Put("Phoenix/Binaries/Win64/game.exe");
        _source.Detect(Root).Should().BeNull();
    }

    [Fact]
    public void Detect_GroupsTheDefaultManifestBySourceMod()
    {
        Put("Phoenix/Content/a.pak");
        Put("Phoenix/Content/b.pak");
        Put("Phoenix/Content/c.pak");
        Manifest("vortex.deployment.json", @"Z:\games\Hogwarts Legacy",
            (@"Phoenix\Content\a.pak", "Alpha-11-1-0-1700000001", ""),
            (@"Phoenix\Content\b.pak", "Alpha-11-1-0-1700000001", ""),
            (@"Phoenix\Content\c.pak", "Beta-22-3-1700000002", ""));

        var found = _source.Detect(Root)!;

        found.Manager.Should().Be("Vortex");
        found.MissingFiles.Should().BeEmpty();
        found.Mods.Select(m => m.Source).Should().Equal("Alpha-11-1-0-1700000001", "Beta-22-3-1700000002");
        found.Mods[0].Files.Should().BeEquivalentTo([Game("Phoenix/Content/a.pak"), Game("Phoenix/Content/b.pak")]);
        found.Mods[1].Files.Should().BeEquivalentTo([Game("Phoenix/Content/c.pak")]);
    }

    [Fact]
    public void Detect_ReadsEveryModTypeManifestAndMergesASourceAcrossThem()
    {
        Put("Merged/hook.dll");
        Put("Phoenix/Content/Paks/~mods/skin_P.pak");
        Manifest("vortex.deployment.dinput.json", @"Z:\steamapps\common\Hogwarts Legacy",
            (@"Merged\hook.dll", "Hook-5-2-0-1700000003", ""));
        Manifest("vortex.deployment.pak.json", @"Z:\steamapps\common\Hogwarts Legacy\Phoenix\Content\Paks\~mods",
            (@"skin_P.pak", "Hook-5-2-0-1700000003", ""));

        var found = _source.Detect(Root)!;

        var hook = found.Mods.Should().ContainSingle().Subject;
        hook.Files.Should().BeEquivalentTo([Game("Merged/hook.dll"), Game("Phoenix/Content/Paks/~mods/skin_P.pak")]);
    }

    [Fact]
    public void Detect_PutsATargetSubfolderBeforeTheRelativePath()
    {
        Put("Phoenix/Content/Movies/intro.bk2");
        Manifest("vortex.deployment.json", @"Z:\x", (@"intro.bk2", "Intro-9-1-1700000004", @"Phoenix\Content\Movies"));

        _source.Detect(Root)!.Mods.Single().Files.Should().Equal(Game("Phoenix/Content/Movies/intro.bk2"));
    }

    [Fact]
    public void Detect_SkipsAFileThatIsNotOnDiskAndListsIt()
    {
        Put("here.pak");
        Manifest("vortex.deployment.json", @"Z:\x", ("here.pak", "Mod-1-1-1700000005", ""), ("gone.pak", "Mod-1-1-1700000005", ""));

        var found = _source.Detect(Root)!;

        found.Mods.Single().Files.Should().Equal(Game("here.pak"));
        found.MissingFiles.Should().Equal(Game("gone.pak"));
    }

    [Fact]
    public void Detect_DropsAModWhoseFilesAreAllMissing()
    {
        Manifest("vortex.deployment.json", @"Z:\x", ("gone.pak", "Mod-1-1-1700000005", ""));

        var found = _source.Detect(Root)!;

        found.Mods.Should().BeEmpty();
        found.MissingFiles.Should().Equal(Game("gone.pak"));
    }

    [Fact]
    public void Detect_StillAdoptsAFileWhoseBytesChangedSinceDeployment()
    {
        var path = Put("tweak.ini", "as deployed");
        Manifest("vortex.deployment.json", @"Z:\x", ("tweak.ini", "Tweak-3-1-0-1700000006", ""));
        File.WriteAllText(path, "edited by the player afterwards");

        _source.Detect(Root)!.Mods.Single().Files.Should().Equal(Game("tweak.ini"));
    }

    [Fact]
    public void Detect_UsesTheSpellingOnDiskWhenTheManifestCasesDiffer()
    {
        Put("Merged/Hook.DLL");
        Manifest("vortex.deployment.json", @"Z:\x", (@"merged\hook.dll", "Hook-5-2-0-1700000003", ""));

        _source.Detect(Root)!.Mods.Single().Files.Should().Equal(Game("Merged/Hook.DLL"));
    }

    [Theory]
    [InlineData(@"..\outside.dll", "")]
    [InlineData(@"Merged\..\..\outside.dll", "")]
    [InlineData(@"fine.dll", @"..\up")]
    [InlineData(@"C:\Windows\x.dll", "")]
    [InlineData(@"\absolute.dll", "")]
    public void Detect_RefusesAPathThatLeavesTheGameFolder(string relPath, string target)
    {
        Put("fine.dll");
        Manifest("vortex.deployment.json", @"Z:\x", ("fine.dll", "Ok-1-1-1700000007", ""), (relPath, "Bad-2-1-1700000008", target));

        var act = () => _source.Detect(Root);

        act.Should().Throw<ForeignDeploymentRefusedException>().WithMessage("*game was not added*vortex.deployment.json*outside the game folder*Purge*Vortex*try again*");
    }

    [Fact]
    public void Detect_SkipsALinkThatPointsOutsideTheGameFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs symbolic links");
        var target = Path.Combine(_outside, "elsewhere.dll");
        File.WriteAllText(target, "not the game's");
        File.CreateSymbolicLink(Path.Combine(_root, "linked.dll"), target);
        Put("inside.dll");
        Manifest("vortex.deployment.json", @"Z:\x", ("linked.dll", "Mod-1-1-1700000009", ""), ("inside.dll", "Mod-1-1-1700000009", ""));

        var found = _source.Detect(Root)!;

        found.Mods.Single().Files.Should().Equal(Game("inside.dll"));
    }

    [Fact]
    public void Detect_SkipsAFileBehindAFolderLinkThatPointsOutsideTheGameFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs symbolic links");
        var elsewhere = Path.Combine(_outside, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "a.dll"), "not the game's");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Escape"), elsewhere);
        Manifest("vortex.deployment.json", @"Z:\x", (@"Escape\a.dll", "Mod-1-1-1700000009", ""));

        _source.Detect(Root)!.Mods.Should().BeEmpty();
    }

    [Fact]
    public void Detect_SkipsADeepFileBehindAFolderLinkThatPointsOutsideTheGameFolder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Needs symbolic links");
        var deep = string.Join('/', Enumerable.Range(0, 20).Select(i => "d" + i));
        var elsewhere = Path.Combine(_outside, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, deep));
        File.WriteAllText(Path.Combine(elsewhere, deep, "a.dll"), "not the game's");
        Directory.CreateSymbolicLink(Path.Combine(_root, "Escape"), elsewhere);
        Manifest("vortex.deployment.json", @"Z:\x", (@"Escape\" + deep.Replace('/', '\\') + @"\a.dll", "Mod-1-1-1700000009", ""));

        _source.Detect(Root)!.Mods.Should().BeEmpty();
    }

    [Fact]
    public void Detect_LeavesAFileAloneWhenVortexBackedUpTheOriginal()
    {
        Put("Data/replaced.pak");
        Put("Data/replaced.pak.vortex_backup", "the game's own");
        Put("Data/new.pak");
        Manifest("vortex.deployment.json", @"Z:\x", (@"Data\replaced.pak", "Mod-1-1-1700000009", ""), (@"Data\new.pak", "Mod-1-1-1700000009", ""));

        var found = _source.Detect(Root)!;

        found.Mods.Single().Files.Should().Equal(Game("Data/new.pak"));
        found.ReplacedFiles.Should().Equal(Game("Data/replaced.pak"));
    }

    [Fact]
    public void Detect_IgnoresAManifestThatIsNotJsonAndReadsTheOthers()
    {
        Put("ok.pak");
        File.WriteAllText(Path.Combine(_root, "vortex.deployment.broken.json"), "{ not json");
        Manifest("vortex.deployment.json", @"Z:\x", ("ok.pak", "Ok-1-1-1700000010", ""));

        _source.Detect(Root)!.Mods.Single().Files.Should().Equal(Game("ok.pak"));
    }

    [Theory]
    [InlineData("vortex.deployment.json", true)]
    [InlineData("vortex.deployment.dinput.json", true)]
    [InlineData("VORTEX.DEPLOYMENT.pak.json", true)]
    [InlineData("vortex.deployment.json.bak", false)]
    [InlineData("vortex.json", false)]
    [InlineData("Phoenix/vortex.deployment.json", false)]
    [InlineData("__folder_managed_by_vortex", true)]
    [InlineData("Phoenix/Content/Paks/~mods/__folder_managed_by_vortex", true)]
    [InlineData("Phoenix/Content/readme.txt", false)]
    public void IsRecordFile_RecognisesManifestsAndMarkersByName(string path, bool expected)
    {
        _source.IsRecordFile(Game(path)).Should().Be(expected);
    }

    [Fact]
    public void IsRecordFile_DoesNotClaimAFileInAnotherLocation()
    {
        _source.IsRecordFile(new GamePath(LocationId.Preferences, "vortex.deployment.json")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Better Combat-1234-1-0-2-1700000000", "Better Combat", 1234u, "1.0.2")]
    [InlineData("Name-With-Dashes-77-2-1699999999", "Name-With-Dashes", 77u, "2")]
    [InlineData("Mod 2023 Edition-55-1-0b-1700000000", "Mod 2023 Edition", 55u, "1.0b")]
    [InlineData("Only Id-9-1700000000", "Only Id", 9u, null)]
    [InlineData("Mod-1234-v1-2-1700000000", "Mod", 1234u, "v1.2")]
    [InlineData("Mod-1234-v1-1700000000", "Mod", 1234u, "v1")]
    [InlineData("Mod-1234-beta-1-1700000000", "Mod", 1234u, "beta.1")]
    [InlineData("Mod-1234-0-1700000000", "Mod", 1234u, "0")]
    public void ParseSource_StripsTheIdVersionAndTime(string source, string display, uint id, string? version)
    {
        var parsed = VortexDeploymentSource.ParseSource(source);

        parsed.DisplayName.Should().Be(display);
        parsed.NexusModId!.Value.Value.Should().Be(id);
        parsed.Version.Should().Be(version);
    }

    [Theory]
    [InlineData("Bundled - My Collection")]
    [InlineData("Hand Made Mod")]
    [InlineData("Odd-1234")]
    [InlineData("Mod-12ab-1-0-1700000000")]
    [InlineData("-1234-1-0-1700000000")]
    [InlineData("1234-1-0-1700000000")]
    [InlineData("Mod-0-1700000000")]
    [InlineData("Mod-0-1-1700000000")]
    [InlineData("Mod-99999999999-1-1700000000")]
    public void ParseSource_KeepsANameThatDoesNotCarryAnId(string source)
    {
        var parsed = VortexDeploymentSource.ParseSource(source);

        parsed.DisplayName.Should().Be(source);
        parsed.NexusModId.Should().BeNull();
        parsed.Version.Should().BeNull();
    }

    [Fact]
    public void Detect_NamesAModAfterItsSourceAndKeepsTheFullSource()
    {
        Put("a.pak");
        Put("b.pak");
        Manifest("vortex.deployment.json", @"Z:\x", ("a.pak", "Better Combat-1234-1-0-2-1700000000", ""), ("b.pak", "Bundled - My Collection", ""));

        var mods = _source.Detect(Root)!.Mods;

        var combat = mods.Single(m => m.Source == "Better Combat-1234-1-0-2-1700000000");
        combat.DisplayName.Should().Be("Better Combat");
        combat.NexusModId!.Value.Value.Should().Be(1234u);
        combat.Version.Should().Be("1.0.2");
        mods.Single(m => m.Source == "Bundled - My Collection").DisplayName.Should().Be("Bundled - My Collection");
    }
}
