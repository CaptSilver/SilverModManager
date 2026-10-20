using FluentAssertions;

namespace NexusMods.Deployment.Vfs.Tests;

public class InodeTableTests
{
    [Fact]
    public void NumbersCountUpFromTwoAndStayPut()
    {
        var table = new InodeTable();
        var a = table.GetOrAdd(1, new VfsNameKey("Data"));
        var b = table.GetOrAdd(1, new VfsNameKey("Other"));

        a.Should().Be(2);
        b.Should().Be(3);
        table.GetOrAdd(1, new VfsNameKey("DATA")).Should().Be(a);
    }

    [Fact]
    public void MoveKeepsTheNumberUnderTheNewName()
    {
        var table = new InodeTable();
        var ino = table.GetOrAdd(1, new VfsNameKey("a.esp"));

        table.Move(1, new VfsNameKey("a.esp"), 5, new VfsNameKey("b.esp")).Should().BeTrue();

        table.GetOrAdd(5, new VfsNameKey("B.ESP")).Should().Be(ino);
        table.Move(1, new VfsNameKey("a.esp"), 5, new VfsNameKey("c.esp")).Should().BeFalse();
    }
}
