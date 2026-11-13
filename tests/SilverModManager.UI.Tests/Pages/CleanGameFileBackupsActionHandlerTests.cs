using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.DataModel;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The clean-up button on the backup-space warning asks first, and only an accepted dialog removes anything.
/// </summary>
public class CleanGameFileBackupsActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private (CleanGameFileBackupsActionHandler Handler, IWindowManager Windows, IDiagnosticManager Diagnostics) Build(ButtonDefinitionId answer)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(Task.FromResult(new StandardDialogResult { ButtonId = answer }));
        var diagnostics = Substitute.For<IDiagnosticManager>();
        var services = new ServiceCollection().AddSingleton(Connection).BuildServiceProvider();
        return (new CleanGameFileBackupsActionHandler(services, windows, Provider.GetRequiredService<GameBackupSpace>(), diagnostics), windows, diagnostics);
    }

    private async Task<FileIdentity> RootAStrayBackup(Loadout.ReadOnly loadout)
    {
        var bytes = "a backup nothing needs"u8.ToArray();
        var identity = FileIdentity.XxHash3(bytes.xxHash3());
        await Provider.GetRequiredService<IFileStore>().BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("stray", new MemoryStream(bytes)), identity, Size.FromLong(bytes.Length))]);
        await GameBackedUpFile.Root(Connection, loadout.InstallationId, [identity]);
        return identity;
    }

    private bool IsRooted(Loadout.ReadOnly loadout, FileIdentity identity)
        => GameBackedUpFile.All(Connection.Db).Any(f => f.GameInstallId == loadout.InstallationId && f.Hash == identity.XxHash3());

    [Fact]
    public async Task AcceptingRemovesTheUnneededBackupAndRefreshes()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Clean Backups Accept");
        var stray = await RootAStrayBackup(loadout);
        var (handler, windows, diagnostics) = Build(ButtonDefinitionId.Accept);

        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.CleanGameFileBackups().Arguments);

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        IsRooted(loadout, stray).Should().BeFalse();
        await diagnostics.Received(1).RefreshAsync(loadout.LoadoutId);
    }

    [Fact]
    public async Task CancellingRemovesNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Clean Backups Cancel");
        var stray = await RootAStrayBackup(loadout);
        var (handler, _, diagnostics) = Build(ButtonDefinitionId.Cancel);

        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.CleanGameFileBackups().Arguments);

        IsRooted(loadout, stray).Should().BeTrue();
        await diagnostics.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    public async Task WithNothingToRemove_AsksNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Clean Backups None");
        var (handler, windows, _) = Build(ButtonDefinitionId.Accept);

        await handler.ExecuteAsync(loadout.LoadoutId, DiagnosticActions.CleanGameFileBackups().Arguments);

        await windows.DidNotReceiveWithAnyArgs().ShowDialog(default!, default);
    }
}
