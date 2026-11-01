using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using R3;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

public interface ILoadoutViewModel : IPageViewModelInterface
{
    string EmptyStateTitleText { get; }

    LoadoutTreeDataGridAdapter Adapter { get; }
    IReadOnlyBindableReactiveProperty<int> ItemCount { get; }
    IReadOnlyBindableReactiveProperty<int> SelectionCount { get; }

    LoadoutPageSubTabs SelectedSubTab { get; }
    bool HasRulesSection { get; }
    ISortingSelectionViewModel RulesSectionViewModel { get; }

    bool IsCollection { get; }

    /// <summary>What the page accepts when something is dragged onto it: non-null only on a collection's page. The "All" page takes no drops.</summary>
    IDropTarget? DropTarget { get; }

    /// <summary>Receives a drop that fails. Set whenever <see cref="DropTarget"/> is.</summary>
    IErrorPresenter? ErrorPresenter { get; }

    /// <summary>
    /// What dragging <paramref name="rows"/> out of the page carries: the loadout items they stand for, or null when
    /// they stand for none.
    /// </summary>
    DragPayload? DragPayloadFor(IReadOnlyList<CompositeItemModel<EntityId>> rows);
    IReadOnlyBindableReactiveProperty<bool> IsCollectionUploaded { get; }
    IReadOnlyBindableReactiveProperty<string> CollectionName { get; }
    IReadOnlyBindableReactiveProperty<CollectionStatus> CollectionStatus { get; }
    IReadOnlyBindableReactiveProperty<RevisionStatus> RevisionStatus { get; }
    IReadOnlyBindableReactiveProperty<RevisionNumber> RevisionNumber { get; }
    IReadOnlyBindableReactiveProperty<DateTimeOffset> LastUploadedDate { get; }
    IReadOnlyBindableReactiveProperty<bool> HasOutstandingChanges { get; }

    ReactiveCommand<NavigationInformation> CommandOpenLibraryPage { get; }
    ReactiveCommand<NavigationInformation> CommandOpenFilesPage { get; }

    ReactiveCommand<Unit> CommandRemoveItem { get; }
    ReactiveCommand<Unit> CommandDeselectItems { get; }

    ReactiveCommand<Unit> CommandRenameGroup { get; }
    ReactiveCommand<Unit> CommandShareCollection { get; }
    ReactiveCommand<Unit> CommandUploadDraftRevision { get; }
    ReactiveCommand<Unit> CommandUploadAndPublishRevision { get; }
    ReactiveCommand<Unit> CommandOpenRevisionUrl { get; }
    ReactiveCommand<Unit> CommandCopyRevisionUrl { get; }
    ReactiveCommand<Unit> CommandChangeVisibility { get; }
    ReactiveCommand<Unit> CommandDeleteGroup { get; }

    // TODO: remove for GA
    bool EnableCollectionSharing { get; }
}
