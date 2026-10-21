using FluentAssertions;
using NexusMods.Backend.Process;

namespace NexusMods.Backend.Tests.OS;

public sealed class ProcessTreeTests : IDisposable
{
    private readonly DirectoryInfo _proc = Directory.CreateTempSubdirectory("proc-fixture-");

    public void Dispose() => _proc.Delete(recursive: true);

    private void AddProcess(int pid, string name, int parent)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_proc.FullName, pid.ToString()));
        File.WriteAllText(Path.Combine(dir.FullName, "stat"), $"{pid} ({name}) S {parent} {pid} {pid} 0 -1 4194560 0 0");
    }

    [Fact]
    public void Descendants_ListsChildrenBeforeGrandchildrenAndLeavesOutTheRest()
    {
        AddProcess(100, "sh", parent: 1);
        AddProcess(101, "host-spawn", parent: 100);
        AddProcess(102, "sleep", parent: 101);
        AddProcess(103, "cat", parent: 100);
        AddProcess(200, "unrelated", parent: 1);
        AddProcess(201, "unrelated child", parent: 200);

        var descendants = ProcessTree.Descendants(100, _proc.FullName);

        descendants.Should().BeEquivalentTo([101, 102, 103]);
        descendants.ToList().IndexOf(102).Should().BeGreaterThan(descendants.ToList().IndexOf(101), "a grandchild comes after its parent");
    }

    [Fact]
    public void Descendants_NameWithSpacesAndParentheses_ReadsTheParentAfterTheName()
    {
        AddProcess(100, "sh", parent: 1);
        AddProcess(101, "odd) 5 (name", parent: 100);

        ProcessTree.Descendants(100, _proc.FullName).Should().Equal(101);
    }

    [Fact]
    public void Descendants_EntriesWithoutAStatFileOrAPid_AreSkipped()
    {
        AddProcess(100, "sh", parent: 1);
        AddProcess(101, "child", parent: 100);
        Directory.CreateDirectory(Path.Combine(_proc.FullName, "102"));
        Directory.CreateDirectory(Path.Combine(_proc.FullName, "self"));

        ProcessTree.Descendants(100, _proc.FullName).Should().Equal(101);
    }

    [Fact]
    public void Descendants_ProcessWithoutChildren_IsEmpty()
    {
        AddProcess(100, "sh", parent: 1);

        ProcessTree.Descendants(100, _proc.FullName).Should().BeEmpty();
    }
}
