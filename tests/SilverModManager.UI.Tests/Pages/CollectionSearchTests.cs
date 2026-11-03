using Avalonia.Threading;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.Filters;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages.Collections;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Installed tab's search box filters the load order tree by what a row shows and by the files its mod deploys.
/// </summary>
public class CollectionSearchTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutFilter NoCollection = new()
    {
        LoadoutId = LoadoutId.From(EntityId.From(0xBEEF)),
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    };

    /// <summary>A tree whose rows are whatever the test hands it, searched through the given file search.</summary>
    private sealed class Probe : LoadoutTreeDataGridAdapter
    {
        private readonly SourceCache<CompositeItemModel<EntityId>, EntityId> _rows = new(static row => row.Key);

        public Probe(IServiceProvider provider, LoadoutFilter filter, IModFileSearch? fileSearch, params (EntityId Key, string Name)[] rows)
            : base(provider, filter, fileSearch ?? new NoFiles())
        {
            _rows.AddOrUpdate(rows.Select(row =>
            {
                var model = new CompositeItemModel<EntityId>(row.Key);
                model.Add(SharedColumns.Name.NameComponentKey, new NameComponent(row.Name));
                return model;
            }));
        }

        protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical) => _rows.Connect();

        public IReadOnlyList<string> VisibleNames() => VisibleRoots
            .Select(row => row.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value)
            .ToArray();
    }

    private sealed class NoFiles : IModFileSearch
    {
        public IReadOnlySet<EntityId> ItemsWithFileMatching(string text) => new HashSet<EntityId>();
    }

    private sealed class FakeFiles(Func<string, IEnumerable<EntityId>> find) : IModFileSearch
    {
        public int Searches;
        public readonly List<bool> RanOnUiThread = [];

        public IReadOnlySet<EntityId> ItemsWithFileMatching(string text)
        {
            Interlocked.Increment(ref Searches);
            lock (RanOnUiThread) RanOnUiThread.Add(Dispatcher.UIThread.CheckAccess());
            return find(text).ToHashSet();
        }
    }

    private static EntityId Id(int n) => EntityId.From((ulong)(0x5000 + n));

    private async Task<(Probe Adapter, IDisposable Activation)> Open(IModFileSearch? files, params (EntityId, string)[] rows)
    {
        var (adapter, activation) = await OnUi(() =>
        {
            var probe = new Probe(Provider, NoCollection, files, rows);
            return Task.FromResult((probe, probe.Activate()));
        });
        await EventuallyOnUi(() => adapter.VisibleNames().Should().HaveCount(rows.Length));
        return (adapter, activation);
    }

    private Task Search(Probe adapter, string text) => OnUi(() =>
        adapter.Filter.Value = string.IsNullOrWhiteSpace(text) ? Filter.NoFilter.Instance : new Filter.TextFilter(text));

    [Fact]
    public async Task ATextThatIsPartOfAModNameKeepsOnlyThatMod()
    {
        var (adapter, activation) = await Open(null, (Id(1), "SkyUI"), (Id(2), "USSEP"), (Id(3), "Skyrim Upscaler"));
        using var _ = activation;

        await Search(adapter, "ussep");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["USSEP"]));
    }

    [Fact]
    public async Task TheNameMatchIgnoresCase()
    {
        var (adapter, activation) = await Open(null, (Id(1), "SkyUI"), (Id(2), "USSEP"));
        using var _ = activation;

        await Search(adapter, "SKYUI");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["SkyUI"]));
    }

    [Fact]
    public async Task AModStaysWhenAFileItDeploysMatches()
    {
        var files = new FakeFiles(text => text == "SkyrimUpscaler.dll" ? [Id(2)] : []);
        var (adapter, activation) = await Open(files, (Id(1), "SkyUI"), (Id(2), "Upscaler Pack"));
        using var _ = activation;

        await Search(adapter, "SkyrimUpscaler.dll");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["Upscaler Pack"]));
    }

    [Fact]
    public async Task AParentStaysWhenOnlyAFileOfItsChildMatches()
    {
        // The search reports the groups above a matching mod too.
        var files = new FakeFiles(_ => [Id(2), Id(1)]);
        var (adapter, activation) = await Open(files, (Id(1), "Wabbajack list"), (Id(2), "Upscaler"), (Id(3), "Unrelated"));
        using var _ = activation;

        await Search(adapter, "dll");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["Wabbajack list", "Upscaler"]));
    }

    [Fact]
    public async Task ClearingTheTextShowsEveryModAgain()
    {
        var (adapter, activation) = await Open(null, (Id(1), "SkyUI"), (Id(2), "USSEP"));
        using var _ = activation;
        await Search(adapter, "ussep");
        await EventuallyOnUi(() => adapter.VisibleNames().Should().HaveCount(1));

        await Search(adapter, "");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["SkyUI", "USSEP"]));
        adapter.HasNoMatches.Value.Should().BeFalse();
    }

    [Fact]
    public async Task ATextThatMatchesNothingReportsNoMatches()
    {
        var (adapter, activation) = await Open(null, (Id(1), "SkyUI"), (Id(2), "USSEP"));
        using var _ = activation;
        adapter.HasNoMatches.Value.Should().BeFalse("nothing is filtered yet");

        await Search(adapter, "nothing like this");

        await EventuallyOnUi(() =>
        {
            adapter.VisibleNames().Should().BeEmpty();
            adapter.HasNoMatches.Value.Should().BeTrue();
        });
    }

    [Fact]
    public async Task AnEmptyListIsNotReportedAsNoMatches()
    {
        var (adapter, activation) = await Open(null);
        using var _ = activation;

        await Search(adapter, "anything");

        await EventuallyOnUi(() => adapter.HasNoMatches.Value.Should().BeFalse("there is nothing to match"));
    }

    [Fact]
    public async Task TheFilesAreSearchedOnceAndOffTheUiThreadHoweverManyRowsThereAre()
    {
        var rows = Enumerable.Range(1, 900).Select(n => (Id(n), $"Mod {n}")).ToArray();
        var files = new FakeFiles(_ => [Id(417)]);
        var (adapter, activation) = await Open(files, rows);
        using var _ = activation;

        await Search(adapter, "skyrimupscaler.dll");

        await EventuallyOnUi(() => adapter.VisibleNames().Should().BeEquivalentTo(["Mod 417"]));
        files.Searches.Should().Be(1, "a row is answered from the set the search returned, not by searching files again");
        files.RanOnUiThread.Should().Equal(false);
    }

    private async Task<EntityId> AddMod(LoadoutId loadout, EntityId parent, string name, params string[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = name, ParentId = parent },
        };
        foreach (var file in files)
        {
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout, Name = file, ParentId = id },
                    TargetPath = new GamePath(LocationId.Game, file).ToGamePathParentTuple(loadout),
                },
                Hash = Hash.From(0xAB),
                Size = Size.From(10),
            };
        }
        return (await tx.Commit()).Remap(group).Id;
    }

    [Fact]
    public async Task TheLoadoutsFilesAreSearchedThroughTheDatabase()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "search-" + Guid.NewGuid().ToString("N"));
        var list = (await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "List")).Id;
        var upscaler = await AddMod(loadout.LoadoutId, list, "Upscaler", "SKSE/Plugins/SkyrimUpscaler.dll", "SKSE/Plugins/UpscalerBasePlugin.dll");
        var skyui = await AddMod(loadout.LoadoutId, list, "SkyUI", "Interface/skyui.swf");
        var elsewhere = await SeedLoadoutRow(GameStore.Steam, "search-other-" + Guid.NewGuid().ToString("N"));
        _ = await AddMod(elsewhere.LoadoutId, list, "Other loadout", "SKSE/Plugins/SkyrimUpscaler.dll");

        var search = new LoadoutFileSearch(Connection, loadout.LoadoutId);

        search.ItemsWithFileMatching("skyrimupscaler.dll").Should().BeEquivalentTo([upscaler, list]);
        search.ItemsWithFileMatching("UpscalerBasePlugin").Should().BeEquivalentTo([upscaler, list]);
        search.ItemsWithFileMatching("skyui.swf").Should().BeEquivalentTo([skyui, list]);
        search.ItemsWithFileMatching("missing.dll").Should().BeEmpty();
    }

    [Fact]
    public async Task AModAddedAfterTheFirstSearchIsFoundByTheNext()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "search-grow-" + Guid.NewGuid().ToString("N"));
        var list = (await CollectionSeed.AddLocalGroup(Connection, loadout.LoadoutId, "List")).Id;
        _ = await AddMod(loadout.LoadoutId, list, "First", "a/first.dll");
        var search = new LoadoutFileSearch(Connection, loadout.LoadoutId);
        search.ItemsWithFileMatching("late.dll").Should().BeEmpty();

        var late = await AddMod(loadout.LoadoutId, list, "Late", "a/late.dll");

        search.ItemsWithFileMatching("late.dll").Should().BeEquivalentTo([late, list]);
    }
}
