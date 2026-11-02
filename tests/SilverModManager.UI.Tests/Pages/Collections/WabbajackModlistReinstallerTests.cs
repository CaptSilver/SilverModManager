using System.Reactive;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using ReactiveUI;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.LibraryPage.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages.Collections;

/// <summary>The more menu's Reinstall for a Wabbajack modlist: what it refuses, what it asks, what it removes and what it starts.</summary>
public class WabbajackModlistReinstallerTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Fixture
    {
        public required Loadout.ReadOnly Loadout { get; init; }
        public required WabbajackModlistLibraryFile.ReadOnly File { get; init; }
        public required EntityId Group { get; init; }
        public required EntityId Archive { get; init; }
        public required IWabbajackModlistViewModel Downloads { get; init; }
        public int InstallsStarted { get; set; }
        public List<IJob> Running { get; } = [];
        public required IJobMonitor Monitor { get; init; }
        public required IWindowManager Windows { get; init; }
        public required IErrorPresenter Errors { get; init; }
        public required ICollectionPageHost Host { get; init; }
        public required WabbajackModlistReinstaller Reinstaller { get; init; }
    }

    private async Task<Fixture> Open(ButtonDefinitionId answer, bool canInstall = true, string blockedReason = "")
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, $"Reinstall {Guid.NewGuid()}");
        var file = await CollectionSeed.AddModlistFile(Connection, $"Reinstall list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, loadout.LoadoutId, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        await AddMod(loadout.LoadoutId, group, "Mod A", fileCount: 2);
        await AddMod(loadout.LoadoutId, group, "Mod B", fileCount: 1);
        var archive = await CollectionSeed.AddOwnedArchive(Connection, file.Id, "archive-a.7z");

        var monitor = Substitute.For<IJobMonitor>();
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal).Returns(new StandardDialogResult { ButtonId = answer });
        var errors = Substitute.For<IErrorPresenter>();
        var host = Substitute.For<ICollectionPageHost>();
        host.WindowManager.Returns(windows);

        var downloads = Substitute.For<IWabbajackModlistViewModel>();
        downloads.CanInstall.Returns(canInstall);
        downloads.InstallBlockedReason.Returns(blockedReason);
        downloads.PrepareInstallAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var fixture = new Fixture
        {
            Loadout = loadout,
            File = file,
            Group = group,
            Archive = archive,
            Downloads = downloads,
            Monitor = monitor,
            Windows = windows,
            Errors = errors,
            Host = host,
            Reinstaller = new WabbajackModlistReinstaller(
                Connection,
                Provider.GetRequiredService<ILoadoutManager>(),
                errors,
                new ModlistInstallRuns(monitor),
                downloads,
                LibraryFileId.From(file.Id),
                () => loadout.LoadoutId),
        };
        downloads.InstallCommand.Returns(ReactiveCommand.Create(() => { fixture.InstallsStarted++; }));
        monitor.Snapshot().Returns(_ => fixture.Running.ToArray());
        return fixture;
    }

    private async Task AddMod(LoadoutId loadout, EntityId parent, string name, int fileCount)
    {
        using var tx = Connection.BeginTransaction();
        var mod = new LoadoutItemGroup.New(tx, out var modId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, modId) { LoadoutId = loadout, Name = name, ParentId = LoadoutItemGroupId.From(parent) },
        };
        for (var i = 0; i < fileCount; i++)
        {
            var path = new GamePath(LocationId.Game, $"Data/{name}-{i}.esp");
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout, ParentId = mod.Id, Name = path.Path.FileName },
                    TargetPath = path.ToGamePathParentTuple(loadout),
                },
                Hash = Hash.From(0xC0FFEE),
                Size = Size.From(10),
            };
        }
        await tx.Commit();
    }

    private IEnumerable<LoadoutItem.ReadOnly> Contents(Fixture f) => LoadoutItem.FindByParent(Connection.Db, f.Group);

    private int FilesInLoadout(Fixture f) => LoadoutItem.All(Connection.Db).Count(item => item.LoadoutId == f.Loadout.LoadoutId && item.Name.StartsWith("Mod ") && item.Name.EndsWith(".esp"));

    /// <summary>A job the monitor lists as an unfinished install of the list into the loadout.</summary>
    private static IJob RunningInstall(Fixture f)
    {
        var job = Substitute.For<IJob>();
        job.Status.Returns(JobStatus.Running);
        job.Definition.Returns(new InstallModlistJob
        {
            TargetLoadout = f.Loadout.LoadoutId,
            Modlist = f.File,
            Options = new InstallOptions(false, new HashSet<RelativePath>()),
            Connection = null!,
            FileStore = null!,
            Inventory = null!,
            Transforms = null!,
            PrefixResolver = null!,
            Paths = null!,
            Writer = null!,
            Openers = null!,
            Logger = null!,
            OrderImporter = null!,
        });
        return job;
    }

    private static Task Run(Fixture f) => Dispatcher.UIThread.InvokeAsync(() => f.Reinstaller.Run(f.Host));

    [Fact]
    public async Task ItRefusesWhileAnInstallIntoTheLoadoutRunsAndChangesNothing()
    {
        var f = await Open(ButtonDefinitionId.Accept);
        f.Running.Add(RunningInstall(f));

        var refused = await Record.ExceptionAsync(() => Run(f));

        refused.Should().BeOfType<InstallAlreadyRunningException>().Which.Should().BeAssignableTo<UserFacingException>();
        Contents(f).Should().HaveCount(2);
        f.InstallsStarted.Should().Be(0);
        await f.Windows.DidNotReceiveWithAnyArgs().ShowDialog(default!, default);
    }

    [Fact]
    public async Task ItRefusesBeforeAskingWhenTheInstallWouldNotStart()
    {
        var f = await Open(ButtonDefinitionId.Accept, canInstall: false, blockedReason: "1 of 3 archives is not in your library yet.");

        var refused = await Record.ExceptionAsync(() => Run(f));

        refused.Should().BeOfType<ModlistReinstallRefusedException>().Which.Message.Should().Contain("1 of 3 archives is not in your library yet.");
        Contents(f).Should().HaveCount(2);
        f.InstallsStarted.Should().Be(0);
        await f.Windows.DidNotReceiveWithAnyArgs().ShowDialog(default!, default);
    }

    [Fact]
    public async Task ItRefusesAListThatIsNotInstalledInTheLoadout()
    {
        var f = await Open(ButtonDefinitionId.Accept);
        await WabbajackGroupSeed.Delete(Connection, f.Group);

        var refused = await Record.ExceptionAsync(() => Run(f));

        refused.Should().BeOfType<ModlistReinstallRefusedException>();
        f.InstallsStarted.Should().Be(0);
    }

    [Fact]
    public async Task CancellingTheDialogChangesNothing()
    {
        var f = await Open(ButtonDefinitionId.Cancel);

        await Run(f);

        Contents(f).Should().HaveCount(2);
        FilesInLoadout(f).Should().Be(3);
        f.InstallsStarted.Should().Be(0);
        await f.Windows.Received(1).ShowDialog(Arg.Any<IDialog>(), DialogWindowType.Modal);
    }

    [Fact]
    public async Task ConfirmingRemovesTheModsAndStartsOneInstall()
    {
        var f = await Open(ButtonDefinitionId.Accept);

        await Run(f);

        Contents(f).Should().BeEmpty("the mods' groups and their files are gone");
        FilesInLoadout(f).Should().Be(0);
        f.InstallsStarted.Should().Be(1);
        await f.Downloads.Received(1).PrepareInstallAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheModlistGroupAndTheArchivesStay()
    {
        var f = await Open(ButtonDefinitionId.Accept);

        await Run(f);

        WabbajackModlistLoadoutGroup.Load(Connection.Db, f.Group).IsValid().Should().BeTrue("the group is what the install resumes into");
        LibraryFile.Load(Connection.Db, f.Archive).IsValid().Should().BeTrue("nothing is downloaded again");
        WabbajackModlistArchive.FindByModlist(Connection.Db, f.File.Id).Should().ContainSingle();
        WabbajackModlistLibraryFile.Load(Connection.Db, f.File.Id).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task TheGroupReadsAsNotFinishedOnceItsContentsAreGone()
    {
        var f = await Open(ButtonDefinitionId.Accept);
        WabbajackModlistInstallState.Read(Connection.Db, LibraryFileId.From(f.File.Id), f.Loadout.LoadoutId).Should().Be(WabbajackModlistCardState.Installed);

        await Run(f);

        WabbajackModlistInstallState.Read(Connection.Db, LibraryFileId.From(f.File.Id), f.Loadout.LoadoutId)
            .Should().Be(WabbajackModlistCardState.Incomplete, "an install that fails to start must leave something Resume can pick up");
    }

    [Fact]
    public async Task TheMenuEntryShowsARefusalInsteadOfThrowingIt()
    {
        var f = await Open(ButtonDefinitionId.Accept);
        f.Running.Add(RunningInstall(f));

        await Dispatcher.UIThread.InvokeAsync(() => f.Reinstaller.Reinstall(f.Host));

        await f.Errors.Received(1).Report(Arg.Is<Exception>(e => e is InstallAlreadyRunningException), Arg.Any<string>());
        Contents(f).Should().HaveCount(2);
    }
}
