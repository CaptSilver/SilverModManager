using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Windows;

/// <summary>Which game a dropped mod is recorded for: the loadout the drop landed on, or none.</summary>
public class LibraryDropActionsAcquisitionTests(IServiceProvider provider) : AUiTest(provider)
{
    private AbsolutePath Archive(string entryName)
    {
        var root = Directory.CreateTempSubdirectory("drop-acquisition-").FullName;
        var path = Path.Combine(root, entryName + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName + ".txt").Open());
            writer.Write(entryName);
        }
        return FileSystem.Shared.FromUnsanitizedFullPath(path);
    }

    private LibraryDropActions ActionsOn(IWorkspaceContext workspaceContext)
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Context.Returns(workspaceContext);
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return new LibraryDropActions(Provider.GetRequiredService<ILibraryService>(), Connection, controller);
    }

    [Fact]
    public async Task AModDroppedOnALoadout_IsRecordedForThatLoadoutsGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Drop target");
        var game = loadout.InstallationInstance.Game.GameId;
        var file = Archive("dropped-on-loadout");

        await ActionsOn(new LoadoutContext { LoadoutId = loadout.LoadoutId }).AddMod(file, CancellationToken.None);

        LibraryDestinations.ItemsFor(Connection.Db, game).Should().ContainSingle(item => item.Name == file.FileName);
        LibraryDestinations.Unassigned(Connection.Db).Should().NotContain(item => item.Name == file.FileName);
    }

    [Fact]
    public async Task AModDroppedAnywhereElse_HasNoGame()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Not the target");
        var game = loadout.InstallationInstance.Game.GameId;
        var file = Archive("dropped-elsewhere");

        await ActionsOn(new HomeContext()).AddMod(file, CancellationToken.None);

        LibraryDestinations.Unassigned(Connection.Db).Should().ContainSingle(item => item.Name == file.FileName);
        LibraryDestinations.ItemsFor(Connection.Db, game).Should().NotContain(item => item.Name == file.FileName);
    }
}
