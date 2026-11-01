using System.Collections.ObjectModel;
using NSubstitute;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.LeftMenu.Loadout;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.UI.Tests.LeftMenu;

internal sealed class LoadoutLeftMenuStub : AViewModel<ILoadoutLeftMenuViewModel>, ILoadoutLeftMenuViewModel
{
    private static ILeftMenuItemViewModel Item(string text) => new LeftMenuItemDesignViewModel { Text = new StringComponent(text), Icon = IconValues.Download };

    public LoadoutLeftMenuStub(params ILeftMenuItemViewModel[] collections)
    {
        LeftMenuCollectionItems = new ReadOnlyObservableCollection<ILeftMenuItemViewModel>(new ObservableCollection<ILeftMenuItemViewModel>(collections));
    }

    public List<(int From, int To)> Moves { get; } = [];

    /// <summary>The first entry keeps its place, as the oldest editable collection does.</summary>
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuCollectionItems { get; }
    public bool HasSingleCollection => false;
    public WorkspaceId WorkspaceId { get; } = WorkspaceId.From(Guid.NewGuid());
    public IApplyControlViewModel ApplyControlViewModel { get; } = new ApplyControlDesignViewModel();
    public ILeftMenuItemViewModel LeftMenuItemLibrary { get; } = Item("Library item");
    public ILeftMenuItemViewModel LeftMenuItemLoadout { get; } = Item("All item");
    public ReadOnlyObservableCollection<ILeftMenuItemViewModel> LeftMenuStoreItems { get; } = new([]);
    public ILeftMenuItemViewModel LeftMenuItemNewCollection { get; } = Item("New collection item");
    public ILeftMenuItemViewModel LeftMenuItemHealthCheck { get; } = Item("Health item");
    public ILeftMenuItemViewModel LeftMenuItemTools { get; } = Item("Tools item");
    public ILeftMenuItemViewModel? LeftMenuItemIni => null;
    public ILeftMenuItemViewModel? LeftMenuItemExternalChanges => null;

    public void MoveCollection(int fromIndex, int toIndex) => Moves.Add((fromIndex, toIndex));
    public bool IsCollectionMovable(int index) => index >= 1 && index < LeftMenuCollectionItems.Count;
}

internal static class LeftMenuTestSupport
{
    public static IWorkspaceController Controller()
    {
        var workspace = Substitute.For<IWorkspaceViewModel>();
        workspace.Id.Returns(WorkspaceId.From(Guid.NewGuid()));
        workspace.Panels.Returns(new ReadOnlyObservableCollection<IPanelViewModel>([]));
        var controller = Substitute.For<IWorkspaceController>();
        controller.ActiveWorkspace.Returns(workspace);
        return controller;
    }
}
