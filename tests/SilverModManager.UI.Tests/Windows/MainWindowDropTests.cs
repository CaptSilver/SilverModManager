using System.IO.Compression;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>Files dropped on the main window: what each one turns into, what the overlay says, and that each kind reaches its action.</summary>
public class MainWindowDropTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("window-drop-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private string Zip(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) archive.CreateEntry(entry);
        return path;
    }

    [Theory]
    [InlineData("list.wabbajack", DropKind.Modlist)]
    [InlineData("LIST.WABBAJACK", DropKind.Modlist)]
    [InlineData("mod.zip", DropKind.Mod)]
    [InlineData("mod.7z", DropKind.Mod)]
    [InlineData("mod.RAR", DropKind.Mod)]
    [InlineData("notes.txt", DropKind.Refused)]
    [InlineData("noextension", DropKind.Refused)]
    public void EachKindOfFile_IsClassifiedByItsExtension(string name, DropKind expected)
    {
        var plan = MainWindowDropPlan.Classify([File(name)]);

        plan.Items.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    [Fact]
    public void ARefusedFile_SaysWhy()
    {
        var plan = MainWindowDropPlan.Classify([File("notes.txt")]);

        plan.Items[0].Reason.Should().Contain(".zip").And.Contain(".7z").And.Contain(".rar");
    }

    [Fact]
    public void ACollectionManifest_IsRefusedBecauseCollectionsComeFromNexus()
    {
        var plan = MainWindowDropPlan.Classify([File("collection.json", "{}")]);

        plan.Items[0].Kind.Should().Be(DropKind.Refused);
        plan.Items[0].Reason.Should().Contain("collection");
    }

    [Fact]
    public void AnArchiveHoldingACollectionManifest_IsRefusedNotAddedAsAMod()
    {
        var plan = MainWindowDropPlan.Classify([Zip("collection.zip", "collection.json", "bundled/readme.txt")]);

        plan.Items[0].Kind.Should().Be(DropKind.Refused);
        plan.Items[0].Reason.Should().Contain("collection");
    }

    [Fact]
    public void AnArchiveThatIsNotAZip_IsStillAMod()
    {
        var plan = MainWindowDropPlan.Classify([File("broken.zip", "not a zip")]);

        plan.Items[0].Kind.Should().Be(DropKind.Mod);
    }

    [Fact]
    public void ADirectory_IsRefused()
    {
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);

        var plan = MainWindowDropPlan.Classify([folder]);

        plan.Items[0].Kind.Should().Be(DropKind.Refused);
        plan.Items[0].Reason.Should().Contain("folder");
    }

    [Fact]
    public void AMissingFile_IsRefused()
    {
        var plan = MainWindowDropPlan.Classify([Path.Combine(_root, "gone.zip")]);

        plan.Items[0].Kind.Should().Be(DropKind.Refused);
        plan.Items[0].Reason.Should().Contain("exist");
    }

    [Fact]
    public void AMixedDrop_KeepsEveryFileInOrderWithItsOwnKind()
    {
        var plan = MainWindowDropPlan.Classify([File("a.zip"), File("list.wabbajack"), File("b.7z"), File("c.txt")]);

        plan.Items.Select(item => item.Kind).Should().Equal(DropKind.Mod, DropKind.Modlist, DropKind.Mod, DropKind.Refused);
        plan.Mods.Should().HaveCount(2);
        plan.Modlists.Should().HaveCount(1);
        plan.Refused.Should().HaveCount(1);
    }

    [Fact]
    public void ADroppedPathListedTwice_IsHandledOnce()
    {
        var path = File("a.zip");

        MainWindowDropPlan.Classify([path, path]).Items.Should().HaveCount(1);
    }

    [Fact]
    public void TheOverlay_NamesWhatWillHappen()
    {
        MainWindowDropPlan.Classify([File("a.zip"), File("b.zip"), File("c.zip")]).OverlayText.Should().Be("Add 3 mods to the library");
        MainWindowDropPlan.Classify([File("one.7z")]).OverlayText.Should().Be("Add 1 mod to the library");
        MainWindowDropPlan.Classify([File("Cool List.wabbajack")]).OverlayText.Should().Be("Open modlist Cool List");
        MainWindowDropPlan.Classify([File("x.wabbajack"), File("y.wabbajack")]).OverlayText.Should().Be("Open 2 modlists");
        MainWindowDropPlan.Classify([File("Cool List.wabbajack"), File("a.zip"), File("b.zip")]).OverlayText
            .Should().Be("Open modlist Cool List, add 2 mods to the library");
    }

    [Fact]
    public void TheOverlay_ForOnlyRefusedFiles_SaysNothingWillBeAdded()
    {
        MainWindowDropPlan.Classify([File("notes.txt")]).OverlayText.Should().Be("Can't add notes.txt");
        MainWindowDropPlan.Classify([File("notes.txt"), File("more.txt")]).OverlayText.Should().Be("Can't add 2 files");
    }

    [Fact]
    public void TheOverlay_ForAMixedDrop_MentionsTheFilesItWillSkip()
    {
        MainWindowDropPlan.Classify([File("a.zip"), File("notes.txt")]).OverlayText.Should().Be("Add 1 mod to the library (skipping 1 file)");
    }

    private sealed class Fixture
    {
        public IMainWindowDropActions Actions { get; } = Substitute.For<IMainWindowDropActions>();
        public IWindowNotificationService Notifications { get; } = Substitute.For<IWindowNotificationService>();
        public MainWindowDropHandler Handler { get; }

        public Fixture(string? loadoutName = null)
        {
            Actions.CurrentLoadoutName.Returns(loadoutName);
            Actions.AddModlist(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(LibraryFileId.From(EntityId.From(7))));
            Actions.AddMod(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            Handler = new MainWindowDropHandler(Actions, Notifications);
        }
    }

    [Fact]
    public async Task AModlist_IsAddedThenItsPageOpens()
    {
        var fixture = new Fixture();

        await fixture.Handler.HandleAsync([File("list.wabbajack")], CancellationToken.None);

        await fixture.Actions.Received(1).AddModlist(Arg.Is<AbsolutePath>(p => p.FileName == "list.wabbajack"), Arg.Any<CancellationToken>());
        fixture.Actions.Received(1).OpenModlist(Arg.Any<LibraryFileId>());
    }

    [Fact]
    public async Task EveryModArchive_GoesThroughTheLibraryJob()
    {
        var fixture = new Fixture();

        await fixture.Handler.HandleAsync([File("a.zip"), File("b.7z"), File("c.rar")], CancellationToken.None);

        await fixture.Actions.Received(3).AddMod(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>());
        await fixture.Actions.DidNotReceiveWithAnyArgs().AddModlist(default, default);
    }

    [Fact]
    public async Task AddedMods_GetOneToastNamingTheLoadoutToInstallInto()
    {
        var fixture = new Fixture(loadoutName: "Skyrim - Main");

        await fixture.Handler.HandleAsync([File("a.zip"), File("b.zip")], CancellationToken.None);

        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.Contains("Added 2 mods") && text.Contains("Skyrim - Main")),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task WithoutAnOpenLoadout_TheToastOnlyReportsTheAdd()
    {
        var fixture = new Fixture(loadoutName: null);

        await fixture.Handler.HandleAsync([File("a.zip")], CancellationToken.None);

        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text == "Added 1 mod to the library"),
            Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task RefusedFiles_AreListedInOneNotificationWithTheirReasons()
    {
        var fixture = new Fixture();

        await fixture.Handler.HandleAsync([File("a.txt"), File("b.txt"), Path.Combine(_root, "gone.zip")], CancellationToken.None);

        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.Contains("a.txt") && text.Contains("b.txt") && text.Contains("gone.zip") && text.Contains("exist")),
            ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        await fixture.Actions.DidNotReceiveWithAnyArgs().AddMod(default, default);
    }

    [Fact]
    public async Task AFileTheJobRejects_JoinsTheRefusalsAndTheRestStillGetAdded()
    {
        var fixture = new Fixture();
        fixture.Actions.AddMod(Arg.Is<AbsolutePath>(p => p.FileName == "bad.zip"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        await fixture.Handler.HandleAsync([File("bad.zip"), File("good.zip")], CancellationToken.None);

        await fixture.Actions.Received(2).AddMod(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>());
        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.Contains("bad.zip") && text.Contains("disk full")),
            ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.StartsWith("Added 1 mod")),
            Arg.Is<ToastNotificationVariant>(v => v != ToastNotificationVariant.Failure), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }

    [Fact]
    public async Task AModlistTheLibraryDoesNotRecognise_IsReportedNotOpened()
    {
        var fixture = new Fixture();
        fixture.Actions.AddModlist(Arg.Any<AbsolutePath>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LibraryFileId>(new InvalidOperationException("not a modlist")));

        await fixture.Handler.HandleAsync([File("list.wabbajack")], CancellationToken.None);

        fixture.Actions.DidNotReceiveWithAnyArgs().OpenModlist(default);
        fixture.Notifications.Received(1).ShowToast(
            Arg.Is<string>(text => text.Contains("list.wabbajack") && text.Contains("not a modlist")),
            ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
    }
}
