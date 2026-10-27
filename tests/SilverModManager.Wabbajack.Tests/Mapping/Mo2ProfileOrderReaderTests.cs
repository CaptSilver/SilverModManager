using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Tests.Install;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Tests.Mapping;

public class Mo2ProfileOrderReaderTests(ITestOutputHelper helper) : InstallTestBase<Mo2ProfileOrderReaderTests>(helper)
{
    private static readonly IReadOnlySet<string> NoRoots = new HashSet<string>();

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private async Task<(Dictionary<string, LoadoutItemGroup.ReadOnly> Groups, LoadoutItemGroup.ReadOnly[] Children)> Groups(params string[] names)
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId parent;
        using (var tx = Connection.BeginTransaction())
        {
            var parentGroup = new LoadoutItemGroup.New(tx, out var parentId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, parentId) { LoadoutId = loadout.LoadoutId, Name = "list" },
            };
            foreach (var name in names)
            {
                var isSettings = name == "<settings>";
                _ = new LoadoutItemGroup.New(tx, out var id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        LoadoutId = loadout.LoadoutId,
                        Name = isSettings ? "C" : name,
                        ParentId = parentGroup.LoadoutItemGroupId,
                    },
                };
                if (isSettings) tx.Add(id, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
            }
            var result = await tx.Commit();
            parent = result.Remap(parentGroup).LoadoutItemGroupId;
        }

        var children = LoadoutItem.FindByParent(Connection.Db, parent).OfTypeLoadoutItemGroup().ToArray();
        return (children.ToDictionary(c => c.IsGameSettingsGroup ? "<settings>" : c.AsLoadoutItem().Name), children);
    }

    private static ModlistTxtEntry[] Entries(string text) => ModlistTxt.Parse(text).ToArray();

    [Fact]
    public async Task OverwriteThenListedTopDownThenUnlistedThenSettingsThenRoots()
    {
        var (groups, children) = await Groups("Root", "Overwrite", "<settings>", "A", "B", "U", "V");
        var entries = Entries("+B\n+Sep_separator\n+A\n");
        var logger = new RecordingLogger();

        var order = Mo2ProfileOrderReader.Read(children, entries, new HashSet<string> { "Root" }, "My list", logger);

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(
            groups["Overwrite"].LoadoutItemGroupId,
            groups["B"].LoadoutItemGroupId,
            groups["A"].LoadoutItemGroupId,
            groups["V"].LoadoutItemGroupId,
            groups["U"].LoadoutItemGroupId,
            groups["<settings>"].LoadoutItemGroupId,
            groups["Root"].LoadoutItemGroupId);
        order.Dropped.Should().BeEmpty();
        logger.Warnings.Should().HaveCount(2).And.OnlyContain(w => w.Contains("has no line in modlist.txt") && w.Contains("My list"));
    }

    [Fact]
    public async Task ADisabledLineGivesEnabledFalseAndASettingsGroupNamedLikeAModIsLeftNull()
    {
        var (groups, children) = await Groups("A", "C", "<settings>", "Overwrite");

        var order = Mo2ProfileOrderReader.Read(children, Entries("+A\n-C\n"), NoRoots, "list", NullLogger.Instance);

        bool? EnabledOf(string key) => order.WinnerToLoser.Single(e => e.Group == groups[key].LoadoutItemGroupId).Enabled;
        EnabledOf("A").Should().BeTrue();
        EnabledOf("C").Should().BeFalse();
        EnabledOf("<settings>").Should().BeNull("the -C line is about the mod, not the settings group");
        EnabledOf("Overwrite").Should().BeNull();
    }

    [Fact]
    public async Task AListedNameWithNoGroupIsDropped()
    {
        var (groups, children) = await Groups("A");

        var order = Mo2ProfileOrderReader.Read(children, Entries("+Ghost\n+A\n*Dlc.esm\n+Sep_separator\n"), NoRoots, "list", NullLogger.Instance);

        order.WinnerToLoser.Select(e => e.Group).Should().Equal(groups["A"].LoadoutItemGroupId);
        order.Dropped.Should().ContainSingle().Which.Should().Be(new ModOrderDrop("Ghost", "no mod of that name was installed"));
    }

    [Fact]
    public async Task NoModlistTxtOrdersEveryModAlphabeticallyWithNoDrops()
    {
        var (groups, children) = await Groups("B", "A", "Overwrite");
        var logger = new RecordingLogger();

        var order = Mo2ProfileOrderReader.Read(children, [], NoRoots, "list", logger);

        // Alphabetical from the bottom up, as the install has always ranked unlisted mods.
        order.WinnerToLoser.Select(e => e.Group).Should().Equal(
            groups["Overwrite"].LoadoutItemGroupId, groups["B"].LoadoutItemGroupId, groups["A"].LoadoutItemGroupId);
        order.Dropped.Should().BeEmpty();
        logger.Warnings.Should().HaveCount(2);
    }
}
