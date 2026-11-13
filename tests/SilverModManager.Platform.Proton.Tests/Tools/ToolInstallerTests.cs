using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>
/// Adding a tool means pointing at a folder or a program: what the catalogue knows is saved with its id, and any
/// other program is saved as the plain program it is.
/// </summary>
public class ToolInstallerTests(ITestOutputHelper helper) : AToolFlowTest<ToolInstallerTests>(helper)
{
    private ToolInstaller Installer() => new(Connection, new KnownToolDetector([TexGen, DynDolod]));

    private AbsolutePath Folder(params string[] files)
    {
        var folder = TemporaryFileManager.CreateFolder("tools").Path;
        foreach (var file in files) File.WriteAllText((folder / file).GetFullPath(), "exe");
        return folder;
    }

    private List<UserTool.ReadOnly> Rows(Loadout.ReadOnly loadout) =>
        UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).OrderBy(t => t.Name).ToList();

    [Fact]
    public async Task AFolderSavesEachCatalogueToolItHoldsWithItsId()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("TexGenx64.exe", "DynDOLODx64.exe", "readme.txt");

        var result = await Installer().Add(loadout, folder);

        result.Added.Should().Equal("DynDOLOD", "TexGen");
        var rows = Rows(loadout);
        rows.Select(r => (r.Name, r.ToolId.Value, r.Exe)).Should().Equal(
            ("DynDOLOD", "dyndolod", (folder / "DynDOLODx64.exe").GetFullPath()),
            ("TexGen", "texgen", (folder / "TexGenx64.exe").GetFullPath()));
    }

    [Fact]
    public async Task AddingTheSameFolderAgainSavesNothingNew()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("TexGenx64.exe");
        await Installer().Add(loadout, folder);

        var result = await Installer().Add(loadout, folder);

        result.Added.Should().BeEmpty();
        result.AlreadyAdded.Should().Equal("TexGen");
        Rows(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task AProgramTheCatalogueKnowsIsSavedWithItsId()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("DynDOLOD.exe");

        await Installer().Add(loadout, folder / "DynDOLOD.exe");

        Rows(loadout).Should().ContainSingle().Which.ToolId.Value.Should().Be("dyndolod");
    }

    [Fact]
    public async Task AProgramNoDefinitionKnowsIsSavedAsAPlainProgramNamedAfterItsFile()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("MyPatcher.exe");

        var result = await Installer().Add(loadout, folder / "MyPatcher.exe");

        result.Added.Should().Equal("MyPatcher");
        var row = Rows(loadout).Should().ContainSingle().Subject;
        row.Name.Should().Be("MyPatcher");
        row.ToolId.HasValue.Should().BeFalse();
        row.Exe.Should().Be((folder / "MyPatcher.exe").GetFullPath());
    }

    [Fact]
    public async Task AFolderWithNoKnownToolIsRefusedAndSavesNothing()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("readme.txt");

        var act = () => Installer().Add(loadout, folder);

        (await act.Should().ThrowAsync<ToolRefusedException>()).Which.Message.Should().Contain(folder.GetFullPath()).And.Contain("no known tool");
        Rows(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task APathThatDoesNotExistIsRefused()
    {
        var loadout = await CreateLoadout();
        var missing = TemporaryFileManager.CreateFolder("tools").Path / "nothing-here";

        var act = () => Installer().Add(loadout, missing);

        (await act.Should().ThrowAsync<ToolRefusedException>()).Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task BackfillWritesToolIdsOnce()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("DynDOLODx64.exe", "MyPatcher.exe");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new UserTool.New(tx) { InstallationId = loadout.InstallationId, Name = "DynDOLOD", Exe = (folder / "DynDOLODx64.exe").GetFullPath(), Arguments = "-x", RunVia = ToolRunVia.Auto };
            _ = new UserTool.New(tx) { InstallationId = loadout.InstallationId, Name = "MyPatcher", Exe = (folder / "MyPatcher.exe").GetFullPath(), Arguments = "", RunVia = ToolRunVia.Auto };
            await tx.Commit();
        }

        (await Installer().BackfillKnownTools(loadout)).Should().Be(1);

        var rows = Rows(loadout);
        rows.Single(r => r.Name == "DynDOLOD").ToolId.Value.Should().Be("dyndolod");
        rows.Single(r => r.Name == "DynDOLOD").Arguments.Should().Be("-x", "the row's own arguments are not touched");
        rows.Single(r => r.Name == "MyPatcher").ToolId.HasValue.Should().BeFalse();
        (await Installer().BackfillKnownTools(loadout)).Should().Be(0);
    }

    [Fact]
    public async Task AddToolWithKeepOutputMarksThePlainProgramOnly()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("MyPatcher.exe");

        await Installer().Add(loadout, folder / "MyPatcher.exe", keepOutput: true);

        UserTool.CapturesGameFolderOutput.Contains(Rows(loadout).Single()).Should().BeTrue();
    }

    [Fact]
    public async Task AddToolWithoutKeepOutputLeavesTheMarkerOff()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("MyPatcher.exe");

        await Installer().Add(loadout, folder / "MyPatcher.exe");

        UserTool.CapturesGameFolderOutput.Contains(Rows(loadout).Single()).Should().BeFalse();
    }

    [Fact]
    public async Task KeepOutputForAProgramTheCatalogueKnowsIsRefusedAndSavesNothing()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("DynDOLOD.exe");

        var act = () => Installer().Add(loadout, folder / "DynDOLOD.exe", keepOutput: true);

        (await act.Should().ThrowAsync<ToolRefusedException>()).Which.Message.Should().Contain("catalogue");
        Rows(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepOutputForAFolderIsRefusedAndSavesNothing()
    {
        var loadout = await CreateLoadout();
        var folder = Folder("DynDOLOD.exe");

        var act = () => Installer().Add(loadout, folder, keepOutput: true);

        await act.Should().ThrowAsync<ToolRefusedException>();
        Rows(loadout).Should().BeEmpty();
    }
}
