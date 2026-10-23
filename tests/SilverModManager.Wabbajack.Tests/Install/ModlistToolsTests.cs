using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

public class ModlistToolsTests(ITestOutputHelper helper) : InstallTestBase<ModlistToolsTests>(helper)
{
    private static readonly byte[] EditBytes = "sse edit"u8.ToArray();
    private static readonly byte[] ExplorerBytes = "explorer"u8.ToArray();

    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    /// <summary>The three entries a list made with Mod Organizer lists by default, spelled as the compiler writes them.</summary>
    private const string DefaultExecutables =
        "[customExecutables]\nsize=3\n" +
        "1\\arguments=\n1\\binary={--||GAME_PATH_MAGIC_FORWARD||--}/SkyrimSE.exe\n1\\title=Skyrim Special Edition\n" +
        "2\\arguments=\n2\\binary={--||GAME_PATH_MAGIC_FORWARD||--}/SkyrimSELauncher.exe\n2\\title=Skyrim Special Edition Launcher\n" +
        "3\\arguments=\\\"{--||GAME_PATH_MAGIC_DOUBLE_BACK||--}\\\\data\\\"\n3\\binary={--||MO2_PATH_MAGIC_FORWARD||--}/explorer++/Explorer++.exe\n3\\title=Explore Virtual Folder\n";

    private const string SseEditEntry =
        "4\\arguments=-D:\\\"{--||GAME_PATH_MAGIC_DOUBLE_BACK||--}\\\\data\\\"\n4\\binary={--||MO2_PATH_MAGIC_FORWARD||--}/xEdit/SSEEdit.exe\n4\\title=SSEEdit\n";

    private ListBuilder ListWith(string executables, bool withTools = true)
    {
        var list = new ListBuilder();
        var exe = GameExeArchive();
        list.Archives.Add(exe);
        list.Directives.AddRange(
        [
            list.Remapped("ModOrganizer.ini", "[General]\nselected_profile=@ByteArray(Default)\n\n" + executables),
            list.Inline("profiles/Default/modlist.txt", "+A\n"),
            list.Inline("mods/A/A.esp", "a"),
            new SilverModManager.Wabbajack.Format.FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new SilverModManager.Wabbajack.Format.ArchiveHashPath(exe.XxHash64, [])),
        ]);
        if (withTools)
        {
            list.Directives.Add(list.Inline("xEdit\\SSEEdit.exe", EditBytes));
            list.Directives.Add(list.Inline("explorer++\\Explorer++.exe", ExplorerBytes));
        }
        return list;
    }

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLoadoutGroup.ReadOnly Group, InstallModlistJob Job)> InstallList(ListBuilder list)
    {
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (job, group) = await Install(loadout, modlist, Options);
        return (loadout, group, job);
    }

    private UserTool.ReadOnly[] ToolsOf(Loadout.ReadOnly loadout)
        => UserTool.FindByInstallation(Connection.Db, loadout.InstallationId).ToArray();

    [Fact]
    public async Task AnExtraFileIsWrittenUnderTheListsFolderInTheExtrasRootAndNowhereElse()
    {
        var (_, group, _) = await InstallList(ListWith(DefaultExecutables));

        var paths = ServiceProvider.GetRequiredService<WabbajackPaths>();
        var folder = ExtrasOf(group);
        var written = folder.Combine("xEdit/SSEEdit.exe");
        written.FileExists.Should().BeTrue();
        (await written.ReadAllBytesAsync()).Should().Equal(EditBytes);

        var everything = Directory.EnumerateFiles(paths.ExtrasRoot.ToString(), "*", SearchOption.AllDirectories)
            .Select(f => f[(paths.ExtrasRoot.ToString().Length + 1)..].Replace('\\', '/'))
            .ToArray();
        everything.Should().BeEquivalentTo([$"{folder.FileName}/xEdit/SSEEdit.exe"], "Mod Organizer's own explorer++ folder is not a tool folder");

        TreeOf(group).Values.SelectMany(g => g.Keys).Should().NotContain(k => k.Contains("SSEEdit", StringComparison.OrdinalIgnoreCase));
        Group(group).ExtrasFolder.Value.Should().Be(folder.ToString());
    }

    [Fact]
    public async Task TheDefaultExecutablesOfAListAreNotRegisteredAsTools()
    {
        var (loadout, _, job) = await InstallList(ListWith(DefaultExecutables));

        ToolsOf(loadout).Should().BeEmpty("the game, its launcher and Explorer++ are excluded");
        job.UnregisteredTools.Should().BeEmpty("excluded entries are not failures");
    }

    [Fact]
    public async Task AnExecutableInTheExtrasFolderBecomesAToolWithHostPaths()
    {
        var (loadout, group, _) = await InstallList(ListWith(DefaultExecutables + SseEditEntry));

        var tool = ToolsOf(loadout).Should().ContainSingle().Subject;
        var extras = ExtrasOf(group);
        var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty)).ToString();

        tool.Name.Should().Be("SSEEdit");
        tool.Exe.Should().Be(extras.Combine("xEdit/SSEEdit.exe").ToString());
        tool.Arguments.Should().Be($"-D:\"{gameRoot}/data\"");
        tool.RunVia.Should().Be(ToolRunVia.Auto);
    }

    [Fact]
    public async Task APathArgumentOfAToolIsTranslatedForTheWinePrefix()
    {
        var entry = "4\\arguments=\\\"{--||GAME_PATH_MAGIC_DOUBLE_BACK||--}\\\\data\\\"\n4\\binary={--||MO2_PATH_MAGIC_FORWARD||--}/xEdit/SSEEdit.exe\n4\\title=SSEEdit\n";
        var (loadout, _, _) = await InstallList(ListWith(DefaultExecutables + entry));

        var tool = ToolsOf(loadout).Should().ContainSingle().Subject;
        var argument = tool.Arguments.Trim('"');
        var gameRoot = loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty)).ToString();

        // The runner translates each argument with WindowsPathTranslator, which only rewrites arguments that are a host path.
        WindowsPathTranslator.Translate(argument, "/no/prefix").Should().Be("Z:" + (gameRoot + "/data").Replace('/', '\\'));
    }

    [Fact]
    public async Task ABinaryOutsideTheExtrasAndGameFoldersIsReportedAndRegistersNothing()
    {
        var outside = "4\\arguments=\n4\\binary=/usr/bin/true\n4\\title=Outside\n";
        var (loadout, _, job) = await InstallList(ListWith(DefaultExecutables + outside));

        ToolsOf(loadout).Should().BeEmpty();
        job.UnregisteredTools.Should().ContainSingle().Which.Should().Contain("Outside");
    }

    [Fact]
    public async Task ABinaryThatDoesNotExistIsReported()
    {
        var missing = "4\\arguments=\n4\\binary={--||MO2_PATH_MAGIC_FORWARD||--}/Missing/Tool.exe\n4\\title=Missing\n";
        var (loadout, _, job) = await InstallList(ListWith(DefaultExecutables + missing));

        ToolsOf(loadout).Should().BeEmpty();
        job.UnregisteredTools.Should().ContainSingle().Which.Should().Contain("Missing");
    }

    [Fact]
    public async Task InstallingAgainDoesNotRegisterTheSameToolTwice()
    {
        var list = ListWith(DefaultExecutables + SseEditEntry);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        await Install(loadout, modlist, Options);
        await Install(loadout, modlist, Options);

        ToolsOf(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task InstallingAgainKeepsTheFolderTheGroupAlreadyRecords()
    {
        var list = ListWith(DefaultExecutables + SseEditEntry);
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, group) = await Install(loadout, modlist, Options);

        // A group from before folders carried the group's id records a folder named after the list alone.
        var recorded = ServiceProvider.GetRequiredService<WabbajackPaths>().ExtrasRoot.Combine("Test list");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group.Id, WabbajackModlistLoadoutGroup.ExtrasFolder, recorded.ToString());
            await tx.Commit();
        }

        await Install(loadout, modlist, Options);

        Group(group).ExtrasFolder.Value.Should().Be(recorded.ToString());
        recorded.Combine("xEdit/SSEEdit.exe").FileExists.Should().BeTrue();
        ToolsOf(loadout).Should().Contain(t => t.Exe == recorded.Combine("xEdit/SSEEdit.exe").ToString());
    }

    private WabbajackModlistLoadoutGroup.ReadOnly Group(WabbajackModlistLoadoutGroup.ReadOnly stale)
        => WabbajackModlistLoadoutGroup.Load(Connection.Db, stale.Id);
}
