using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Tests.Framework;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>A row of the Library page can be picked up and dropped on a target elsewhere in the window.</summary>
[Collection(HeadlessUi.Collection)]
public class LibraryPageDragTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class RecordingTarget : IDropTarget
    {
        public List<DragPayload> Dropped { get; } = [];
        public DropVerdict Inspect(DragPayload payload) => new DropVerdict.Accept(DragDropEffects.Copy, "Install here");

        public Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
        {
            Dropped.Add(payload);
            return Task.CompletedTask;
        }
    }

    private async Task<LibraryItemId> SeedLocalFile(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = Hash.From((ulong)Guid.NewGuid().GetHashCode()),
            Size = NexusMods.Paths.Size.From(1024),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { OriginalPath = "/mods/" + name, LibraryFile = file };
        LibraryDestinations.Write(tx, id, new LoadoutAcquisition(loadout.LoadoutId).Destination(Connection.Db));
        var result = await tx.Commit();
        return LibraryItemId.From(result[id]);
    }

    [Fact]
    public async Task DraggingALibraryRow_CarriesItsItemForThisLoadout()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Library page drag " + Guid.NewGuid());
        var wanted = await SeedLocalFile(loadout, "wanted-" + Guid.NewGuid().ToString("N") + ".zip");
        var target = new RecordingTarget();
        Window? window = null;
        Border? drop = null;
        LibraryView? view = null;
        await OnUi(async () =>
        {
            var vm = new LibraryViewModel(
                Substitute.For<IWindowManager>(),
                Provider,
                Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
                loadout.LoadoutId);
            view = new LibraryView { ViewModel = vm };
            drop = new Border { Width = 200, Background = Avalonia.Media.Brushes.Gray };
            Grid.SetColumn(drop, 1);
            var host = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(200, GridUnitType.Pixel) }, Children = { view, drop } };
            window = Track(new Window { Width = 1200, Height = 800, Content = host });
            _ = new DropZone(drop, target, Substitute.For<IErrorPresenter>());
            ShowRendered(window);
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Render(window);
        });

        Control? row = null;
        await EventuallyOnUi(() =>
        {
            Render(window!);
            row = view!.GetVisualDescendants().OfType<TreeDataGridRow>()
                .FirstOrDefault(candidate => candidate.Model is CompositeItemModel<EntityId> model
                    && model.Get<LibraryComponents.LibraryItemIds>(LibraryColumns.Actions.LibraryItemIdsComponentKey).ItemIds.Contains(wanted));
            row.Should().NotBeNull("the seeded file shows as a row");
        });

        await OnUi(async () =>
        {
            var from = row!.TranslatePoint(new Point(row.Bounds.Width / 3, row.Bounds.Height / 2), window!)!.Value;
            var to = drop!.TranslatePoint(new Point(100, 50), window!)!.Value;
            window!.MouseDown(from, MouseButton.Left);
            for (var step = 1; step <= 8; step++)
            {
                window.MouseMove(from + (to - from) * step / 8, RawInputModifiers.LeftMouseButton);
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        });

        target.Dropped.Should().ContainSingle().Which.Should().BeOfType<DragPayload.LibraryItems>()
            .Which.Should().BeEquivalentTo(new { Loadout = loadout.LoadoutId, Items = new[] { wanted } });
    }
}
