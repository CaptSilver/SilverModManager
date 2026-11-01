using Avalonia.Input;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>What the window's files target tells a zone about a drag (Inspect) and the order it does the work in (DropAsync).</summary>
public class FilesDropTargetTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("files-drop-target-").FullName;
    private readonly IMainWindowDropActions _actions = Substitute.For<IMainWindowDropActions>();
    private readonly IWindowNotificationService _notifications = Substitute.For<IWindowNotificationService>();
    private readonly FilesDropTarget _target;

    public FilesDropTargetTests()
    {
        _actions.AddModlist(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(LibraryFileId.From(EntityId.From(7))));
        _actions.AddMod(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _target = new FilesDropTarget(_actions, _notifications, logger: null);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private static DragPayload.DroppedFiles Dropped(params string[] paths) => new(paths);

    [Fact]
    public void AModArchive_IsAcceptedAsACopyThatAddsItToTheLibrary()
    {
        var verdict = _target.Inspect(Dropped(File("mod.zip")));

        verdict.Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 mod to the library"));
    }

    [Fact]
    public void AFolder_IsRefusedWithTheReasonTheOverlayGives()
    {
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);

        var verdict = _target.Inspect(Dropped(folder));

        verdict.Should().Be(new DropVerdict.Refuse("Can't add folder"));
    }

    [Fact]
    public void AMixOfRefusedAndAddableFiles_IsAcceptedAndSaysWhatItSkips()
    {
        var verdict = _target.Inspect(Dropped(File("a.zip"), File("notes.txt")));

        verdict.Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Add 1 mod to the library (skipping 1 file)"));
    }

    [Fact]
    public void LibraryRowsAndMods_AreIgnoredSoNothingHappensOnEmptyWindowSpace()
    {
        _target.Inspect(new DragPayload.LibraryItems(default, [])).Should().BeOfType<DropVerdict.Ignore>();
        _target.Inspect(new DragPayload.LoadoutMods(default, [])).Should().BeOfType<DropVerdict.Ignore>();
    }

    [Fact]
    public async Task AModlistAndAMod_AddTheModlistThenOpenItThenAddTheMod()
    {
        await _target.DropAsync(Dropped(File("a.zip"), File("list.wabbajack")), CancellationToken.None);

        Received.InOrder(() =>
        {
            _actions.AddModlist(Arg.Is<AbsolutePath>(p => p.FileName == "list.wabbajack"), Arg.Any<CancellationToken>());
            _actions.OpenModlist(Arg.Any<LibraryFileId>());
            _actions.AddMod(Arg.Is<AbsolutePath>(p => p.FileName == "a.zip"), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AFailingMod_EndsInTheFailureToastAndTheSuccessToastCountsTheRest()
    {
        _actions.AddMod(Arg.Is<AbsolutePath>(p => p.FileName == "bad.zip"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        await _target.DropAsync(Dropped(File("bad.zip"), File("good.zip"), File("fine.zip")), CancellationToken.None);

        _notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.StartsWith("Added 2 mods")),
            Arg.Is<ToastNotificationVariant>(v => v != ToastNotificationVariant.Failure), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        _notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.Contains("bad.zip") && text.Contains("disk full")),
            ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AnythingButDroppedFiles_IsNotTouched()
    {
        await _target.DropAsync(new DragPayload.LoadoutMods(default, []), CancellationToken.None);

        await _actions.DidNotReceiveWithAnyArgs().AddMod(default, default);
        _notifications.ReceivedCalls().Should().BeEmpty();
    }
}
