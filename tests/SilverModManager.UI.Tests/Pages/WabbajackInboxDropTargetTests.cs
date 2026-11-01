using Avalonia.Input;
using FluentAssertions;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>What the modlist page's inbox says about a drag (Inspect) and where it sends the files (DropAsync).</summary>
public class WabbajackInboxDropTargetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wabbajack-inbox-drop-").FullName;
    private readonly IWabbajackModlistViewModel _page = Substitute.For<IWabbajackModlistViewModel>();
    private readonly WabbajackInboxDropTarget _target;

    public WabbajackInboxDropTargetTests() => _target = new WabbajackInboxDropTarget(_page);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Missing(string name) => Path.Combine(_root, name);

    private static DragPayload.DroppedFiles Dropped(params string[] paths) => new(paths);

    [Fact]
    public void OneFile_IsAcceptedAsACopyIntoTheInbox()
        => _target.Inspect(Dropped(File("a.zip"))).Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 file to the inbox"));

    [Fact]
    public void SeveralFiles_AreCounted()
        => _target.Inspect(Dropped(File("a.zip"), File("b.7z"), File("c.rar")))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Add 3 files to the inbox"));

    [Fact]
    public void AFolderBesideAFile_DoesNotStopTheFileFromBeingAccepted()
        => _target.Inspect(Dropped(Folder("dir"), File("a.zip"))).Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 file to the inbox"));

    [Fact]
    public void OnlyFolders_AreRefusedWithAHintToDropTheArchives()
        => _target.Inspect(Dropped(Folder("one"), Folder("two"))).Should().Be(new DropVerdict.Refuse("Drop the archives, not the folder"));

    [Fact]
    public void OnlyFilesThatAreGone_AreRefusedAsMissing()
        => _target.Inspect(Dropped(Missing("gone.zip"))).Should().Be(new DropVerdict.Refuse("These files don't exist any more"));

    [Fact]
    public void AFolderBesideAMissingFile_IsRefusedAsMissing()
        => _target.Inspect(Dropped(Folder("dir"), Missing("gone.zip"))).Should().Be(new DropVerdict.Refuse("These files don't exist any more"));

    [Fact]
    public void LoadoutMods_AreIgnored()
        => _target.Inspect(new DragPayload.LoadoutMods(LoadoutId.From(EntityId.From(1)), [])).Should().BeOfType<DropVerdict.Ignore>();

    [Fact]
    public void LibraryItems_AreIgnored()
        => _target.Inspect(new DragPayload.LibraryItems(LoadoutId.From(EntityId.From(1)), [])).Should().BeOfType<DropVerdict.Ignore>();

    [Fact]
    public async Task Dropping_HandsTheSamePathsToThePage()
    {
        var paths = new[] { File("a.zip"), File("b.zip") };
        using var cancellation = new CancellationTokenSource();

        await _target.DropAsync(Dropped(paths), cancellation.Token);

        await _page.Received(1).AddDroppedFilesAsync(Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(paths)), cancellation.Token);
    }

    [Fact]
    public async Task DroppingSomethingElse_DoesNothing()
    {
        await _target.DropAsync(new DragPayload.LoadoutMods(LoadoutId.From(EntityId.From(1)), []), CancellationToken.None);

        _page.ReceivedCalls().Should().BeEmpty();
    }
}
