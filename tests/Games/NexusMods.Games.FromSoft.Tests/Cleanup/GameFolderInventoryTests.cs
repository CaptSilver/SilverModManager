using FluentAssertions;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Cleanup;

public class GameFolderInventoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-inventory-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Folder => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_root, "Game"));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, "Game", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ListsFilesFoldersAndLinksWithoutFollowingTheLinks()
    {
        Write("eldenring.exe", "12345");
        Write("sd/sd.bdt", "ab");
        var outside = Path.Combine(_root, "reshade");
        Directory.CreateDirectory(Path.Combine(outside, "Shaders"));
        File.WriteAllText(Path.Combine(outside, "Shaders/a.fx"), "x");
        File.CreateSymbolicLink(Path.Combine(_root, "Game/ReShade_shaders"), Path.Combine(outside, "Shaders"));

        var inventory = GameFolderInventory.Take(Folder);

        inventory.Should().Equal(
            new InventoryEntry("eldenring.exe", InventoryKind.File, 5),
            new InventoryEntry("ReShade_shaders", InventoryKind.Link, 0, Path.Combine(outside, "Shaders")),
            new InventoryEntry("sd", InventoryKind.Directory, 0),
            new InventoryEntry("sd/sd.bdt", InventoryKind.File, 2));
    }

    [Fact]
    public void ADanglingLinkIsListed()
    {
        Write("eldenring.exe", "1");
        File.CreateSymbolicLink(Path.Combine(_root, "Game/dxgi.dll"), "/nonexistent/ReShade64.dll");

        GameFolderInventory.Take(Folder).Should().Contain(new InventoryEntry("dxgi.dll", InventoryKind.Link, 0, "/nonexistent/ReShade64.dll"));
    }

    [Fact]
    public void DescribeSaysKindSizeAndTarget()
    {
        var lines = GameFolderInventory.Describe([
            new InventoryEntry("dxgi.dll", InventoryKind.Link, 0, "/x/ReShade64.dll"),
            new InventoryEntry("eldenring.exe", InventoryKind.File, 5),
            new InventoryEntry("sd", InventoryKind.Directory, 0),
        ]);

        lines.Should().Equal("link  dxgi.dll -> /x/ReShade64.dll", "file  eldenring.exe (5 bytes)", "dir   sd/");
    }

    [Fact]
    public void DiffNamesWhatWasRemovedAddedAndChanged()
    {
        InventoryEntry[] before =
        [
            new("a.dll", InventoryKind.File, 1),
            new("b.dll", InventoryKind.File, 2),
            new("c", InventoryKind.Link, 0, "/one"),
            new("same.txt", InventoryKind.File, 3),
        ];
        InventoryEntry[] after =
        [
            new("b.dll", InventoryKind.File, 5),
            new("c", InventoryKind.Link, 0, "/two"),
            new("d.dll", InventoryKind.File, 1),
            new("same.txt", InventoryKind.File, 3),
        ];

        GameFolderInventory.Diff(before, after).Should().Equal("removed  a.dll", "changed  b.dll", "changed  c", "added    d.dll");
    }

    [Fact]
    public void DiffOfTwoEqualInventoriesIsEmpty()
    {
        InventoryEntry[] same = [new("a.dll", InventoryKind.File, 1)];

        GameFolderInventory.Diff(same, same).Should().BeEmpty();
    }
}
