using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// Folding a game's folder on the host into the one inside its Proton prefix. Everything runs in a temp
/// directory with a recording trash, so no real Documents folder or trash is involved.
/// </summary>
public class HostFolderMoveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("host-folder-move").FullName;
    private readonly ITrash _trash = Substitute.For<ITrash>();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Host => Path.Combine(_root, "host", "My Games", "Skyrim");
    private string Prefix => Path.Combine(_root, "pfx", "My Games", "Skyrim");

    private static void Write(string folder, string relative, string contents)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private void Run() => HostFolderMove.Run(Host, Prefix, _trash);

    [Fact]
    public void AFileOnlyTheHostHas_IsCopiedIn()
    {
        Write(Host, "Saves/save1.ess", "old save");
        Directory.CreateDirectory(Prefix);

        Run();

        File.ReadAllText(Path.Combine(Prefix, "Saves", "save1.ess")).Should().Be("old save");
    }

    [Fact]
    public void AFileBothHave_KeepsThePrefixBytes()
    {
        Write(Host, "Skyrim.ini", "host");
        Write(Prefix, "Skyrim.ini", "prefix");

        Run();

        File.ReadAllText(Path.Combine(Prefix, "Skyrim.ini")).Should().Be("prefix");
    }

    [Fact]
    public void NestedFolders_AreCreatedInThePrefix()
    {
        Write(Host, "a/b/c/deep.txt", "deep");
        Write(Prefix, "a/other.txt", "kept");

        Run();

        File.ReadAllText(Path.Combine(Prefix, "a", "b", "c", "deep.txt")).Should().Be("deep");
        File.ReadAllText(Path.Combine(Prefix, "a", "other.txt")).Should().Be("kept");
    }

    [Fact]
    public void APrefixFolderThatDoesNotExistYet_IsCreated()
    {
        Write(Host, "x.ini", "x");
        Directory.CreateDirectory(Path.GetDirectoryName(Prefix)!);

        Run();

        File.ReadAllText(Path.Combine(Prefix, "x.ini")).Should().Be("x");
    }

    [Fact]
    public void TheHostFolderIsHandedToTheTrash_AfterTheCopy()
    {
        Write(Host, "x.ini", "x");
        Directory.CreateDirectory(Prefix);
        _trash.When(t => t.MoveToTrash(Host)).Do(_ => File.Exists(Path.Combine(Prefix, "x.ini")).Should().BeTrue());

        Run();

        _trash.Received(1).MoveToTrash(Host);
    }

    [Fact]
    public void ALinkInsideTheHostFolder_IsNotFollowedOrCopied()
    {
        var outside = Path.Combine(_root, "outside");
        Write(outside, "secret.txt", "secret");
        Write(Host, "real.ini", "real");
        Directory.CreateSymbolicLink(Path.Combine(Host, "linked-dir"), outside);
        File.CreateSymbolicLink(Path.Combine(Host, "linked-file.txt"), Path.Combine(outside, "secret.txt"));
        Directory.CreateDirectory(Prefix);

        Run();

        File.Exists(Path.Combine(Prefix, "real.ini")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Prefix).Select(Path.GetFileName).Should().Equal("real.ini");
    }

    [Fact]
    public void AHostFolderThatIsItselfALink_Refuses()
    {
        var real = Path.Combine(_root, "real");
        Write(real, "x.ini", "x");
        Directory.CreateDirectory(Path.GetDirectoryName(Host)!);
        Directory.CreateSymbolicLink(Host, real);
        Directory.CreateDirectory(Prefix);

        var act = Run;

        act.Should().Throw<HostFolderMoveRefusedException>();
        _trash.DidNotReceiveWithAnyArgs().MoveToTrash(default!);
        Directory.EnumerateFileSystemEntries(Prefix).Should().BeEmpty();
    }

    [Fact]
    public void ACopyThatFails_StopsBeforeAnythingIsTrashed()
    {
        Write(Host, "sub/x.ini", "x");
        // A file where the host has a folder: the folder cannot be created.
        Write(Prefix, "sub", "i am a file");

        var act = Run;

        act.Should().Throw<HostFolderMoveRefusedException>();
        _trash.DidNotReceiveWithAnyArgs().MoveToTrash(default!);
        File.Exists(Path.Combine(Host, "sub", "x.ini")).Should().BeTrue();
    }

    [Fact]
    public void AMissingPrefixParent_RefusesAndChangesNothing()
    {
        Write(Host, "x.ini", "x");

        var act = Run;

        act.Should().Throw<HostFolderMoveRefusedException>().Which.Message.Should().Contain(Path.GetDirectoryName(Prefix)!);
        Directory.Exists(Path.GetDirectoryName(Prefix)!).Should().BeFalse();
        File.Exists(Path.Combine(Host, "x.ini")).Should().BeTrue();
        _trash.DidNotReceiveWithAnyArgs().MoveToTrash(default!);
    }

    [Fact]
    public void AMissingHostFolder_RefusesAndChangesNothing()
    {
        Directory.CreateDirectory(Prefix);

        var act = Run;

        act.Should().Throw<HostFolderMoveRefusedException>();
        _trash.DidNotReceiveWithAnyArgs().MoveToTrash(default!);
    }

    [Fact]
    public void TheSameFolderTwice_Refuses()
    {
        Write(Host, "x.ini", "x");

        var act = () => HostFolderMove.Run(Host, Host, _trash);

        act.Should().Throw<HostFolderMoveRefusedException>();
        _trash.DidNotReceiveWithAnyArgs().MoveToTrash(default!);
    }

    [Fact]
    public void ATrashThatRefuses_LeavesTheHostFolderAndKeepsTheCopies()
    {
        Write(Host, "x.ini", "x");
        Directory.CreateDirectory(Prefix);
        _trash.When(t => t.MoveToTrash(Host)).Do(_ => throw new HostFolderMoveRefusedException("not on this drive"));

        var act = Run;

        act.Should().Throw<HostFolderMoveRefusedException>();
        File.Exists(Path.Combine(Host, "x.ini")).Should().BeTrue();
    }
}
