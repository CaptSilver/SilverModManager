using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A folder a tool wrote becomes a group of the loadout. It has to beat the mods already installed, because tool
/// output exists to replace what they ship, and a second run replaces the first run's files.
/// </summary>
public class ToolOutputCaptureTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ToolOutputCaptureTests>(helper)
{
    private const string GroupName = "DynDOLOD Output";
    private static readonly GamePath Data = new(LocationId.Game, "Data");
    private static readonly GamePath Mesh = new(LocationId.Game, "Data/meshes/a.nif");

    private ToolOutputCapture Capture => ServiceProvider.GetRequiredService<ToolOutputCapture>();

    private AbsolutePath OutputFolder(params (string Path, string Content)[] files)
    {
        var folder = TemporaryFileManager.CreateFolder().Path;
        foreach (var (path, content) in files)
        {
            var file = folder / RelativePath.FromUnsanitizedInput(path);
            file.Parent.CreateDirectory();
            File.WriteAllText(file.GetFullPath(), content);
        }
        return folder;
    }

    private static readonly string[] IgnoreLogs = ["*.log"];

    private LoadoutItemGroup.ReadOnly GroupsNamed(Loadout.ReadOnly loadout, string name) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Single(g => g.AsLoadoutItem().Name == name);

    private List<(GamePath Path, Hash Hash)> FilesOf(LoadoutItemGroup.ReadOnly group) =>
        Refresh(group).Children.OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(c => (Path: (GamePath)c.AsLoadoutItemWithTargetPath().TargetPath, Hash: c.Hash))
            .OrderBy(f => f.Path.Path.ToString(), StringComparer.Ordinal).ToList();

    private IEnumerable<ConflictPriority> PriorityRows(Loadout.ReadOnly loadout, LoadoutItemGroupId group) =>
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId).Where(r => r.TargetId.Value == group.Value).Select(r => r.Priority);

    private async Task<LoadoutItemGroupId> ModWithPriority(Loadout.ReadOnly loadout, string name, ulong priority, params string[] paths)
    {
        LoadoutItemGroupId group;
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, paths.Select(p => (RelativePath)p), loadout.LoadoutId, name);
            await tx.Commit();
        }
        group = LoadoutItemGroupId.From(GroupsNamed(loadout, name).Id);
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
            await tx.Commit();
        }
        return group;
    }

    [Fact]
    public async Task AFolderBecomesANamedGroupOfFilesUnderTheTargetWithoutTheIgnoredOnes()
    {
        var loadout = await CreateLoadout();
        var folder = OutputFolder(("meshes/a.nif", "mesh"), ("X.esp", "plugin"), ("X.log", "noise"));

        var result = await Capture.CaptureFolder(loadout, folder, Data, GroupName, IgnoreLogs);

        result.FileCount.Should().Be(2);
        var group = GroupsNamed(Refresh(loadout), GroupName);
        group.Id.Should().Be(result.Group.Value);
        FilesOf(group).Select(f => f.Path).Should().Equal(
            new GamePath(LocationId.Game, "Data/X.esp"),
            new GamePath(LocationId.Game, "Data/meshes/a.nif"));
        FilesOf(group).Select(f => f.Hash).Should().BeEquivalentTo(new[] { Encoding.UTF8.GetBytes("mesh").xxHash3(), Encoding.UTF8.GetBytes("plugin").xxHash3() });
    }

    [Fact]
    public async Task CapturedFilesAreBackedUpInTheFileStore()
    {
        var loadout = await CreateLoadout();
        var folder = OutputFolder(("meshes/a.nif", "backed up bytes"));

        await Capture.CaptureFolder(loadout, folder, Data, GroupName, []);

        var identity = FileIdentity.XxHash3(Encoding.UTF8.GetBytes("backed up bytes").xxHash3());
        (await FileStore.HaveFile(identity)).Should().BeTrue();
        (await FileStore.Load(identity)).Should().Equal(Encoding.UTF8.GetBytes("backed up bytes"));
    }

    [Fact]
    public async Task ANewGroupGetsAPriorityRowAboveEveryExistingGroup()
    {
        var loadout = await CreateLoadout();
        var mod = await ModWithPriority(loadout, "Mod", 7, "Data/other.nif");
        var folder = OutputFolder(("meshes/a.nif", "mesh"));

        var result = await Capture.CaptureFolder(loadout, folder, Data, GroupName, []);

        var rows = PriorityRows(Refresh(loadout), result.Group).ToList();
        rows.Should().ContainSingle();
        rows[0].Value.Should().BeGreaterThan(PriorityRows(Refresh(loadout), mod).Single().Value);
    }

    [Fact]
    public async Task ACapturedFileBeatsAModsFileAtTheSamePathAcrossSyncs()
    {
        var loadout = await CreateLoadout();
        await ModWithPriority(loadout, "LOD mod", 5, "Data/meshes/a.nif");
        var folder = OutputFolder(("meshes/a.nif", "captured bytes"));

        await Capture.CaptureFolder(loadout, folder, Data, GroupName, []);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(Mesh);
        (await disk.ReadAllTextAsync()).Should().Be("captured bytes");

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        (await disk.ReadAllTextAsync()).Should().Be("captured bytes", "a second sync must not extract the mod's file back over it");
    }

    [Fact]
    public async Task ASecondCaptureReplacesTheItemsAndKeepsTheGroupAndItsPriorityRow()
    {
        var loadout = await CreateLoadout();
        var first = await Capture.CaptureFolder(loadout, OutputFolder(("meshes/a.nif", "one"), ("old.esp", "gone soon")), Data, GroupName, []);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var priorityBefore = PriorityRows(loadout, first.Group).Single();

        var second = await Capture.CaptureFolder(loadout, OutputFolder(("meshes/a.nif", "two")), Data, GroupName, []);

        second.Group.Should().Be(first.Group);
        FilesOf(GroupsNamed(Refresh(loadout), GroupName)).Select(f => f.Path).Should().Equal(Mesh);
        PriorityRows(Refresh(loadout), second.Group).Should().ContainSingle().Which.Should().Be(priorityBefore);

        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var locations = loadout.InstallationInstance.Locations;
        (await locations.ToAbsolutePath(Mesh).ReadAllTextAsync()).Should().Be("two");
        locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data/old.esp")).FileExists.Should().BeFalse("the second run did not write it");
    }

    [Fact]
    public async Task ACaptureWithNothingToKeepRefusesInsteadOfEmptyingTheGroup()
    {
        var loadout = await CreateLoadout();
        await Capture.CaptureFolder(loadout, OutputFolder(("a.esp", "kept")), Data, GroupName, []);

        var act = () => Capture.CaptureFolder(Refresh(loadout), OutputFolder(("only.log", "noise")), Data, GroupName, IgnoreLogs);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{GroupName}*");
        FilesOf(GroupsNamed(Refresh(loadout), GroupName)).Should().ContainSingle();
    }
}
