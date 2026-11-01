using Avalonia.Input;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// What a collection accepts from a drag, and what it does on the drop: the verdict reads the database, the drop hands
/// the work to <see cref="ILoadoutManager"/> and reports the outcome in a toast.
/// </summary>
public class CollectionDropTargetTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Harness
    {
        public required CollectionDropTarget Target { get; init; }
        public required ILoadoutManager Manager { get; init; }
        public required IWindowNotificationService Toasts { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public required CollectionGroup.ReadOnly Collection { get; init; }
    }

    private async Task<Harness> CreateHarness(bool targetDisabled = false)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Drop target " + Guid.NewGuid());
        var collection = await SeedCollection(loadout, "NCR Core", isReadOnly: false, disabled: targetDisabled);
        return Build(loadout, collection);
    }

    private Harness Build(Loadout.ReadOnly loadout, CollectionGroup.ReadOnly collection)
    {
        var manager = Substitute.For<ILoadoutManager>();
        var toasts = Substitute.For<IWindowNotificationService>();
        var target = new CollectionDropTarget(
            Connection,
            manager,
            Provider.GetRequiredService<ILibraryService>(),
            Provider.GetRequiredService<ICollectionOwnership>(),
            toasts,
            collection.CollectionGroupId);
        return new Harness { Target = target, Manager = manager, Toasts = toasts, Loadout = loadout, Collection = collection };
    }

    private async Task<CollectionGroup.ReadOnly> SeedCollection(Loadout.ReadOnly loadout, string name, bool isReadOnly, bool disabled = false)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = isReadOnly,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
            },
        };
        if (disabled) tx.Add(id, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return CollectionGroup.Load(result.Db, result[id]);
    }

    private async Task<LibraryItem.ReadOnly[]> SeedLibraryItems(params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = name,
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            };
            return file.Id;
        }).ToArray();
        var result = await tx.Commit();
        return added.Select(id => LibraryItem.Load(result.Db, result[id])).ToArray();
    }

    /// <summary>An installed mod: a group in <paramref name="parent"/>, linked to <paramref name="source"/> when there is one.</summary>
    private async Task<LoadoutItemGroupId> SeedMod(Loadout.ReadOnly loadout, string name, CollectionGroup.ReadOnly parent, LibraryItem.ReadOnly? source = null, bool disabled = false)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId, ParentId = parent.Id },
        };
        if (source is { } library) _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = library.Id, LoadoutItemGroup = group };
        if (disabled) tx.Add(id, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private static DragPayload.LibraryItems Library(Loadout.ReadOnly loadout, params LibraryItem.ReadOnly[] items)
        => new(loadout.LoadoutId, items.Select(static item => item.LibraryItemId).ToArray());

    private static DragPayload.LoadoutMods Mods(Loadout.ReadOnly loadout, params LoadoutItemGroupId[] mods)
        => new(loadout.LoadoutId, mods.Select(static mod => LoadoutItemId.From(mod.Value)).ToArray());

    [Fact]
    public async Task ALockedCollection_RefusesLibraryItemsWithItsReason()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Locked " + Guid.NewGuid());
        var nexus = await SeedCollection(loadout, "Big Nexus Collection", isReadOnly: true);
        var items = await SeedLibraryItems("a.7z");
        var harness = Build(loadout, nexus);

        var verdict = harness.Target.Inspect(Library(loadout, items));

        verdict.Should().Be(new DropVerdict.Refuse("Big Nexus Collection is a Nexus collection; it decides what it contains"));
    }

    [Fact]
    public async Task LibraryItemsOfAnotherLoadout_AreRefused()
    {
        var harness = await CreateHarness();
        var other = await SeedSiblingLoadoutRow(harness.Loadout, "Other loadout");
        var items = await SeedLibraryItems("a.7z");

        harness.Target.Inspect(Library(other, items)).Should().Be(new DropVerdict.Refuse("These mods belong to another loadout"));
    }

    [Fact]
    public async Task LibraryItemsAlreadyInTheCollection_AreLeftOutOfTheCount()
    {
        var harness = await CreateHarness();
        var items = await SeedLibraryItems("a.7z", "b.7z", "c.7z");
        await SeedMod(harness.Loadout, "a", harness.Collection, items[0]);

        harness.Target.Inspect(Library(harness.Loadout, items))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Install 2 mods into NCR Core, 1 already there"));
    }

    [Fact]
    public async Task LibraryItemsAllInTheCollection_AreRefused()
    {
        var harness = await CreateHarness();
        var items = await SeedLibraryItems("a.7z", "b.7z");
        await SeedMod(harness.Loadout, "a", harness.Collection, items[0]);
        await SeedMod(harness.Loadout, "b", harness.Collection, items[1]);

        harness.Target.Inspect(Library(harness.Loadout, items)).Should().Be(new DropVerdict.Refuse("Already in NCR Core"));
    }

    [Fact]
    public async Task AnItemLinkedInAnotherCollection_StillCounts()
    {
        var harness = await CreateHarness();
        var elsewhere = await SeedCollection(harness.Loadout, "Elsewhere", isReadOnly: false);
        var items = await SeedLibraryItems("a.7z");
        await SeedMod(harness.Loadout, "a", elsewhere, items[0]);

        harness.Target.Inspect(Library(harness.Loadout, items))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Copy, "Install 1 mod into NCR Core"));
    }

    [Fact]
    public async Task ModsFromAnotherCollection_AreAcceptedAsAMove()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false);
        var one = await SeedMod(harness.Loadout, "one", source);
        var two = await SeedMod(harness.Loadout, "two", source);

        harness.Target.Inspect(Mods(harness.Loadout, one, two))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Move, "Move 2 mods to NCR Core"));
    }

    [Fact]
    public async Task ModsThatCannotMove_AreCountedSeparately()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false);
        var movable = await SeedMod(harness.Loadout, "movable", source);
        var here = await SeedMod(harness.Loadout, "here", harness.Collection);

        harness.Target.Inspect(Mods(harness.Loadout, movable, here))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Move, "Move 1 mod to NCR Core, 1 can't move"));
    }

    [Fact]
    public async Task ModsAlreadyInTheCollection_AreRefusedWithThePlansReason()
    {
        var harness = await CreateHarness();
        var here = await SeedMod(harness.Loadout, "here", harness.Collection);

        harness.Target.Inspect(Mods(harness.Loadout, here)).Should().Be(new DropVerdict.Refuse("here is already in NCR Core"));
    }

    [Fact]
    public async Task ModsMovedIntoADisabledCollection_SayTheyWillBeDisabled()
    {
        var harness = await CreateHarness(targetDisabled: true);
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false);
        var mod = await SeedMod(harness.Loadout, "mod", source);

        harness.Target.Inspect(Mods(harness.Loadout, mod))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Move, "Move 1 mod to NCR Core (they'll be disabled with it)"));
    }

    [Fact]
    public async Task ModsMovedOutOfADisabledCollection_SayTheyWillBeEnabled()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false, disabled: true);
        var mod = await SeedMod(harness.Loadout, "mod", source);
        var alreadyOff = await SeedMod(harness.Loadout, "off", source, disabled: true);

        harness.Target.Inspect(Mods(harness.Loadout, mod, alreadyOff))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Move, "Move 2 mods to NCR Core (they'll be enabled)"));
    }

    [Fact]
    public async Task ADisabledModStaysDisabled_SoItGetsNoEnabledNote()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false, disabled: true);
        var alreadyOff = await SeedMod(harness.Loadout, "off", source, disabled: true);

        harness.Target.Inspect(Mods(harness.Loadout, alreadyOff))
            .Should().Be(new DropVerdict.Accept(DragDropEffects.Move, "Move 1 mod to NCR Core"));
    }

    [Fact]
    public async Task DroppedFiles_AreIgnored()
    {
        var harness = await CreateHarness();

        harness.Target.Inspect(new DragPayload.DroppedFiles(["/tmp/a.zip"])).Should().Be(new DropVerdict.Ignore());
    }

    [Fact]
    public async Task DroppingLibraryItems_InstallsTheOnesNotLinkedAtDropTime()
    {
        var harness = await CreateHarness();
        var items = await SeedLibraryItems("a.7z", "b.7z");
        var payload = Library(harness.Loadout, items);
        harness.Target.Inspect(payload).Should().BeOfType<DropVerdict.Accept>();
        // Linked while the drag was still going.
        await SeedMod(harness.Loadout, "a", harness.Collection, items[0]);
        harness.Manager.InstallItems(default!, default, default).ReturnsForAnyArgs(Task.FromResult(new LoadoutItemGroup.ReadOnly[1]));

        await harness.Target.DropAsync(payload, CancellationToken.None);

        await harness.Manager.Received(1).InstallItems(
            Arg.Is<IReadOnlyList<LibraryItem.ReadOnly>>(installed => installed.Select(item => item.Id).SequenceEqual(new[] { items[1].Id })),
            harness.Loadout.LoadoutId,
            Arg.Is<Optional<LoadoutItemGroupId>>(parent => parent.HasValue && parent.Value.Value == harness.Collection.Id));
        harness.Toasts.Received(1).ShowToast("Installed 1 mod into NCR Core", ToastNotificationVariant.Success);
    }

    [Fact]
    public async Task ABatchThatFailsToInstall_ToastsTheArchiveAndThatNothingWasInstalled()
    {
        var harness = await CreateHarness();
        var items = await SeedLibraryItems("broken.7z");
        harness.Manager.InstallItems(default!, default, default).ReturnsForAnyArgs(
            Task.FromException<LoadoutItemGroup.ReadOnly[]>(new BatchInstallException("broken.7z", new InvalidOperationException("not an archive"))));

        await harness.Target.DropAsync(Library(harness.Loadout, items), CancellationToken.None);

        harness.Toasts.Received(1).ShowToast(
            "Couldn't install into NCR Core: broken.7z: not an archive. Nothing was installed.",
            ToastNotificationVariant.Failure,
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task ACrashWhileInstalling_IsLeftToTheZoneToReport()
    {
        var harness = await CreateHarness();
        var items = await SeedLibraryItems("a.7z");
        harness.Manager.InstallItems(default!, default, default).ReturnsForAnyArgs(Task.FromException<LoadoutItemGroup.ReadOnly[]>(new IOException("disk gone")));

        var drop = async () => await harness.Target.DropAsync(Library(harness.Loadout, items), CancellationToken.None);

        await drop.Should().ThrowAsync<IOException>();
        harness.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!, default, default, default, default);
    }

    [Fact]
    public async Task DroppingMods_MovesThemAndToastsTheCount()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false);
        var one = await SeedMod(harness.Loadout, "one", source);
        var two = await SeedMod(harness.Loadout, "two", source);
        var payload = Mods(harness.Loadout, one, two);
        harness.Manager.MoveItemsToCollection(default!, default).ReturnsForAnyArgs(new CollectionMoveResult([one, two], []));

        await harness.Target.DropAsync(payload, CancellationToken.None);

        await harness.Manager.Received(1).MoveItemsToCollection(payload.Items, harness.Collection.CollectionGroupId);
        harness.Toasts.Received(1).ShowToast("Moved 2 mods to NCR Core", ToastNotificationVariant.Success);
    }

    [Fact]
    public async Task AMoveThatContradictsTheRules_ToastsThatNothingMoved()
    {
        var harness = await CreateHarness();
        var source = await SeedCollection(harness.Loadout, "Source", isReadOnly: false);
        var one = await SeedMod(harness.Loadout, "one", source);
        harness.Manager.MoveItemsToCollection(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("The group rules of loadout 4 contradict each other: 1 and 2."));

        await harness.Target.DropAsync(Mods(harness.Loadout, one), CancellationToken.None);

        harness.Toasts.Received(1).ShowToast(
            "Couldn't move: the load-order rules of NCR Core contradict the rules of these mods. Nothing moved.",
            ToastNotificationVariant.Failure,
            Arg.Any<TimeSpan?>());
    }
}
