using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

/// <summary>
/// A collection as a drop target: library items are installed into it, mods of the same loadout move into it. One
/// instance per collection, whether the drop lands on its left menu entry or on its page. Read-only collections get
/// one too, so the user sees why they refuse.
/// </summary>
public sealed class CollectionDropTarget(
    IConnection connection,
    ILoadoutManager loadoutManager,
    ILibraryService libraryService,
    ICollectionOwnership ownership,
    IWindowNotificationService notifications,
    CollectionGroupId collection) : IDropTarget
{
    /// <summary>The target for <paramref name="collection"/>, wired to the app's services. The menu entry and the page build theirs here.</summary>
    public static CollectionDropTarget For(IServiceProvider services, CollectionGroupId collection) => new(
        services.GetRequiredService<IConnection>(),
        services.GetRequiredService<ILoadoutManager>(),
        services.GetRequiredService<ILibraryService>(),
        services.GetRequiredService<ICollectionOwnership>(),
        services.GetRequiredService<IWindowNotificationService>(),
        collection);

    public DropVerdict Inspect(DragPayload payload) => payload switch
    {
        DragPayload.LibraryItems { Items.Count: > 0 } items => InspectLibraryItems(items),
        DragPayload.LoadoutMods { Items.Count: > 0 } mods => InspectMods(mods),
        _ => new DropVerdict.Ignore(),
    };

    public async Task DropAsync(DragPayload payload, CancellationToken cancellationToken)
    {
        switch (payload)
        {
            case DragPayload.LibraryItems items:
                await InstallAsync(items);
                break;
            case DragPayload.LoadoutMods mods:
                await MoveAsync(mods);
                break;
        }
    }

    private CollectionGroup.ReadOnly Group(IDb db) => CollectionGroup.Load(db, collection);

    private static string NameOf(CollectionGroup.ReadOnly group) => group.AsLoadoutItemGroup().AsLoadoutItem().Name;

    private DropVerdict InspectLibraryItems(DragPayload.LibraryItems payload)
    {
        var group = Group(connection.Db);
        var name = NameOf(group);
        if (ownership.WhyLocked(group) is { } reason) return new DropVerdict.Refuse(reason);
        if (payload.Loadout != group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId) return new DropVerdict.Refuse("These mods belong to another loadout");

        var wanted = payload.Items.Distinct().Count();
        var toInstall = NotYetLinked(payload.Items);
        if (toInstall.Count == 0) return new DropVerdict.Refuse($"Already in {name}");

        var skipped = wanted - toInstall.Count;
        var text = $"Install {ModCount.Text(toInstall.Count)} into {name}" + (skipped > 0 ? $", {skipped} already there" : string.Empty);
        return new DropVerdict.Accept(DragDropEffects.Copy, text);
    }

    private DropVerdict InspectMods(DragPayload.LoadoutMods payload)
    {
        var db = connection.Db;
        var plan = CollectionMovePlan.Plan(db, payload.Items, collection, ownership);
        if (plan.Movable.Count == 0) return new DropVerdict.Refuse(AllAlreadyHere(db, plan) ? $"Already in {plan.TargetName}" : plan.RefusalText);

        var text = $"Move {ModCount.Text(plan.Movable.Count)} to {plan.TargetName}"
                   + (plan.Skipped.Count > 0 ? $", {plan.Skipped.Count} can't move" : string.Empty)
                   + EnabledNote(db, plan.Movable);
        return new DropVerdict.Accept(DragDropEffects.Move, text);
    }

    /// <summary>
    /// True when the plan skipped every mod because it already sits in this collection. The plan's own reasons name
    /// each mod, so they never agree and its <see cref="CollectionMovePlan.RefusalText"/> can't say it in one line.
    /// </summary>
    private bool AllAlreadyHere(IDb db, CollectionMovePlan plan)
        => plan.Skipped.Count > 0
           && ownership.WhyLocked(Group(db)) is null
           && plan.Skipped.All(skip => LoadoutItem.Load(db, skip.Item) is { } item && item.IsValid() && item.HasParent() && item.ParentId.Value == collection.Value);

    /// <summary>
    /// Moving a mod changes whether it is switched on only through the collection: its own switch stays. So mods
    /// that are on now and land in a disabled collection go off, and the other way round.
    /// </summary>
    private string EnabledNote(IDb db, IReadOnlyList<LoadoutItemGroupId> movable)
    {
        var targetOn = Group(db).AsLoadoutItemGroup().AsLoadoutItem().IsEnabled();
        var items = movable.Select(id => LoadoutItem.Load(db, LoadoutItemId.From(id.Value))).ToList();
        if (targetOn is false && items.Any(static item => item.IsEnabled())) return " (they'll be disabled with it)";
        if (targetOn && items.Any(static item => item.IsEnabled() is false && item.IsDisabled is false)) return " (they'll be enabled)";
        return string.Empty;
    }

    /// <summary>The library items with no mod in this collection yet, in the order they were dragged.</summary>
    private List<LibraryItem.ReadOnly> NotYetLinked(IEnumerable<LibraryItemId> ids)
    {
        var db = connection.Db;
        var items = ids.Distinct().Select(id => LibraryItem.Load(db, id)).Where(static item => item.IsValid()).ToList();
        var linkedHere = libraryService.CollectionsWithLibraryItems(items)
            .Where(entry => entry.Key.Id == collection.Value)
            .SelectMany(static entry => entry.Value.Select(static pair => pair.libraryItem.Id))
            .ToHashSet();
        return items.Where(item => linkedHere.Contains(item.Id) is false).ToList();
    }

    private async Task InstallAsync(DragPayload.LibraryItems payload)
    {
        var name = NameOf(Group(connection.Db));
        // The drag may have taken a while; something may have been installed in the meantime.
        var items = NotYetLinked(payload.Items);
        if (items.Count == 0) return;

        try
        {
            var installed = await loadoutManager.InstallItems(items, payload.Loadout, LoadoutItemGroupId.From(collection.Value));
            notifications.ShowToast($"Installed {ModCount.Text(installed.Length)} into {name}", ToastNotificationVariant.Success);
        }
        catch (BatchInstallException failure)
        {
            var why = failure.InnerException?.Message ?? failure.Message;
            notifications.ShowToast($"Couldn't install into {name}: {failure.ItemName}: {why}. Nothing was installed.", ToastNotificationVariant.Failure, TimeSpan.FromSeconds(12));
        }
    }

    private async Task MoveAsync(DragPayload.LoadoutMods payload)
    {
        var name = NameOf(Group(connection.Db));
        try
        {
            var result = await loadoutManager.MoveItemsToCollection(payload.Items, collection);
            if (result.Moved.Count > 0)
            {
                notifications.ShowToast($"Moved {ModCount.Text(result.Moved.Count)} to {name}", ToastNotificationVariant.Success);
                return;
            }

            // Something changed during the drag and nothing can move any more.
            var plan = CollectionMovePlan.Plan(connection.Db, payload.Items, collection, ownership);
            notifications.ShowToast(plan.RefusalText, ToastNotificationVariant.Failure, TimeSpan.FromSeconds(12));
        }
        catch (InvalidOperationException)
        {
            // The resolver refuses an order the stored group rules make impossible; its message is for the log.
            notifications.ShowToast($"Couldn't move: the load-order rules of {name} contradict the rules of these mods. Nothing moved.", ToastNotificationVariant.Failure, TimeSpan.FromSeconds(12));
        }
    }
}
