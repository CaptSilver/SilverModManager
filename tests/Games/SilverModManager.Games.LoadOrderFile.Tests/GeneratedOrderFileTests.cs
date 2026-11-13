using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.LoadOrderFile.Tests;

public class GeneratedOrderFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "generated-order-file-" + Guid.NewGuid().ToString("N"));

    private AbsolutePath Game => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    private static readonly ModFolderModSource ListedOrNoFile = new("Mods", new FolderNamer(new ModOrderTxtFormat()), ExternalListing.WhenListedOrNoFile);

    private static GeneratedOrderFile OrderFile(params IExternalModSource[] sources)
        => new(LoadOrderFileGames.KingdomComeDeliverance with { ExternalMods = sources }, NullLogger<GeneratedOrderFile>.Instance);

    private void Write(string relative, string content = "pak")
    {
        var file = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    private static readonly SyncNodePart Present = new()
    {
        Identity = FileIdentity.XxHash3(new byte[] { 1 }.xxHash3()), Size = Size.From(1), LastModifiedTicks = 0, EntityId = EntityId.From(1),
    };

    private static KeyValuePair<GamePath, SyncNode> Node(string path, LoadoutSourceItemType source)
        => new(new GamePath(LocationId.Game, path), new SyncNode { Actions = Actions.ExtractToDisk, SourceItemType = source, Loadout = Present });

    private static readonly EntityId OverridesRow = EntityId.From(7);

    private static KeyValuePair<GamePath, SyncNode> OverridesNode(string path)
        => new(new GamePath(LocationId.Game, path), new SyncNode { Actions = Actions.DoNothing, SourceItemType = LoadoutSourceItemType.Loadout, Disk = Present, Loadout = Present with { EntityId = OverridesRow } });

    private static bool InOverrides(EntityId item) => item == OverridesRow;

    /// <summary>A file on disk that no loadout row names; its source type is the enum's default.</summary>
    private static KeyValuePair<GamePath, SyncNode> DiskOnly(string path)
        => new(new GamePath(LocationId.Game, path), new SyncNode { Disk = Present });

    private sealed class FixedSource(ExternalListing listing, params string[] names) : IExternalModSource
    {
        public ExternalListing Listing => listing;

        public IReadOnlyList<string> Find(AbsolutePath gameFolder) => names;
    }

    [Fact]
    public void AGameSourcedFolderIsNotManaged()
    {
        var tree = new Dictionary<GamePath, SyncNode>([Node("Mods/Old/a.pak", LoadoutSourceItemType.Game)]);

        OrderFile().ManagedFolders(tree, new Dictionary<string, ulong>(), InOverrides).Should().BeEmpty();
        OrderFile().Render(Game, null, []).Should().BeEmpty();
    }

    [Fact]
    public void AFolderOnlyOnDiskIsNotManaged()
    {
        var tree = new Dictionary<GamePath, SyncNode>([DiskOnly("Mods/Old/a.pak")]);

        OrderFile().ManagedFolders(tree, new Dictionary<string, ulong>(), InOverrides).Should().BeEmpty();
    }

    [Fact]
    public void AFolderTheAppFiledUnderOverridesIsNotManaged()
    {
        var tree = new Dictionary<GamePath, SyncNode>([OverridesNode("Mods/Old/a.pak")]);

        OrderFile().ManagedFolders(tree, new Dictionary<string, ulong>(), InOverrides).Should().BeEmpty();
    }

    [Fact]
    public void ALoadoutSourcedFolderIsManagedLoserFirst()
    {
        var tree = new Dictionary<GamePath, SyncNode>([
            Node("Mods/M2/a.pak", LoadoutSourceItemType.Loadout), Node("Mods/M1/a.pak", LoadoutSourceItemType.Loadout), Node("Mods/Old/a.pak", LoadoutSourceItemType.Game),
        ]);

        OrderFile().ManagedFolders(tree, new Dictionary<string, ulong> { ["M1"] = 1, ["M2"] = 2 }, InOverrides).Should().Equal("M1", "M2");
    }

    [Fact]
    public void AWhenListedOrNoFileSourceListsEveryFolderWhenThereIsNoFile()
    {
        Write("Mods/Old/a.pak");

        OrderFile(ListedOrNoFile).Render(Game, null, []).Should().Be("Old");
    }

    [Fact]
    public void AWhenListedOrNoFileSourceListsOnlyTheListedFoldersWhenThereIsAFile()
    {
        Write("Mods/Old/a.pak");
        Write("Mods/Off/a.pak");

        OrderFile(ListedOrNoFile).Render(Game, "Old", []).Should().Be("Old");
    }

    [Fact]
    public void ExternalsKeepTheFilesOrderAndManagedComeAfterLoserFirst()
    {
        Write("Mods/A/a.pak");
        Write("Mods/B/a.pak");

        OrderFile(ListedOrNoFile).Render(Game, "B\nA", ["M1", "M2"]).Should().Be("B\nA\nM1\nM2");
    }

    [Fact]
    public void AnAlwaysSourceGoesFirstWhenTheFileDoesNotListIt()
    {
        Write("Mods/A/a.pak");

        OrderFile(new FixedSource(ExternalListing.Always, "core"), ListedOrNoFile).Render(Game, "A", []).Should().Be("core\nA");
    }

    [Fact]
    public void AnAlwaysSourceKeepsItsPlaceWhenTheFileListsIt()
    {
        Write("Mods/A/a.pak");

        OrderFile(new FixedSource(ExternalListing.Always, "core"), ListedOrNoFile).Render(Game, "A\ncore", []).Should().Be("A\ncore");
    }

    [Fact]
    public void AWhenListedOrNoFileSourceComesAfterAnAlwaysSourceWithNoFile()
    {
        Write("Mods/A/a.pak");

        OrderFile(ListedOrNoFile, new FixedSource(ExternalListing.Always, "core")).Render(Game, null, ["M1"]).Should().Be("core\nA\nM1");
    }

    [Fact]
    public void AManagedFolderWithAManifestOnDiskIsListedByTheManifestsId()
    {
        Write("Mods/Harmony-2.3.6/About/About.xml", "<ModMetaData><packageId>Brrainz.Harmony</packageId></ModMetaData>");
        var orderFile = new GeneratedOrderFile(
            LoadOrderFileGames.KingdomComeDeliverance with { ExternalMods = [], ManifestReader = new AboutXmlManifestReader() },
            NullLogger<GeneratedOrderFile>.Instance);

        orderFile.Render(Game, null, ["Harmony-2.3.6"]).Should().Be("brrainz.harmony");
    }

    [Fact]
    public void AManagedFolderWhoseFilesAreGoneIsListedByItsFolderName()
    {
        var orderFile = new GeneratedOrderFile(
            LoadOrderFileGames.KingdomComeDeliverance with { ExternalMods = [], ManifestReader = new AboutXmlManifestReader() },
            NullLogger<GeneratedOrderFile>.Instance);

        orderFile.Render(Game, null, ["Harmony-2.3.6"]).Should().Be("Harmony-2.3.6");
    }

    [Fact]
    public void AManagedNameDropsTheExternalDuplicate()
    {
        Write("Mods/Old/a.pak");

        OrderFile(ListedOrNoFile).Render(Game, "Old", ["old"]).Should().Be("old");
    }

    private static readonly ModsConfigXmlFormat Xml = new();

    private static GeneratedOrderFile RimWorldShaped(params IExternalModSource[] sources) => new(
        LoadOrderFileGames.KingdomComeDeliverance with { Format = Xml, ManifestReader = new AboutXmlManifestReader(), ExternalMods = sources },
        NullLogger<GeneratedOrderFile>.Instance);

    private static GameDataFolderModSource CoreSource() => new("Data", new AboutXmlManifestReader(), id => id == "ludeon.rimworld", ExternalListing.Always);

    private void WriteCore() => Write("Data/Core/About/About.xml", "<ModMetaData><packageId>Ludeon.RimWorld</packageId></ModMetaData>");

    private void WriteManaged(string folder, string packageId, params string[] loadBefore)
        => Write($"Mods/{folder}/About/About.xml", $"<ModMetaData><packageId>{packageId}</packageId><loadBefore>{string.Join("", loadBefore.Select(id => $"<li>{id}</li>"))}</loadBefore></ModMetaData>");

    private static string[] OrderOf(string text) => Xml.Listed(text).ToArray();

    [Fact]
    public void AManagedModThatLoadsBeforeAnExternalGoesDirectlyBeforeIt()
    {
        WriteCore();
        WriteManaged("Harmony", "Brrainz.Harmony", "Ludeon.RimWorld");

        var text = RimWorldShaped(CoreSource()).Render(Game, Xml.Render(["ludeon.rimworld"], null), ["Harmony"]);

        OrderOf(text).Should().Equal("brrainz.harmony", "ludeon.rimworld");
    }

    [Fact]
    public void AManagedModWithoutItsManifestOnDiskIsAppended()
    {
        WriteCore();

        var text = RimWorldShaped(CoreSource()).Render(Game, Xml.Render(["ludeon.rimworld"], null), ["Harmony"]);

        OrderOf(text).Should().Equal("ludeon.rimworld", "harmony");
    }

    [Fact]
    public void AManagedModThatLoadsBeforeNothingTheFileHoldsIsAppended()
    {
        WriteCore();
        WriteManaged("Harmony", "Brrainz.Harmony", "Some.Other");

        var text = RimWorldShaped(CoreSource()).Render(Game, Xml.Render(["ludeon.rimworld"], null), ["Harmony"]);

        OrderOf(text).Should().Equal("ludeon.rimworld", "brrainz.harmony");
    }

    [Fact]
    public void AHoistedModGoesAheadOfAManagedModWithNoRule()
    {
        WriteCore();
        WriteManaged("Harmony", "Brrainz.Harmony", "Ludeon.RimWorld");
        WriteManaged("Plain", "Some.Plain");

        var text = RimWorldShaped(CoreSource()).Render(Game, null, ["Plain", "Harmony"]);

        OrderOf(text).Should().Equal("brrainz.harmony", "ludeon.rimworld", "some.plain");
    }

    [Fact]
    public void AHoistedModKeepsItsOrderAmongOtherHoistedMods()
    {
        WriteCore();
        WriteManaged("A", "Mod.A", "ludeon.rimworld");
        WriteManaged("B", "Mod.B", "ludeon.rimworld");

        var text = RimWorldShaped(CoreSource()).Render(Game, null, ["A", "B"]);

        OrderOf(text).Should().Equal("mod.a", "mod.b", "ludeon.rimworld");
    }

    [Fact]
    public void AHoistGoesBeforeTheFirstOfSeveralExternalsItNames()
    {
        WriteCore();
        WriteManaged("Harmony", "Brrainz.Harmony", "Ludeon.RimWorld.Royalty", "Ludeon.RimWorld");
        Write("Data/Royalty/About/About.xml", "<ModMetaData><packageId>Ludeon.RimWorld.Royalty</packageId></ModMetaData>");
        var royalty = new GameDataFolderModSource("Data", new AboutXmlManifestReader(), id => id.StartsWith("ludeon.rimworld."), ExternalListing.WhenListed);

        var text = RimWorldShaped(CoreSource(), royalty).Render(Game, Xml.Render(["ludeon.rimworld", "ludeon.rimworld.royalty"], null), ["Harmony"]);

        OrderOf(text).Should().Equal("brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty");
    }

    [Fact]
    public void AForceLoadBeforeRuleHoistsToo()
    {
        WriteCore();
        Write("Mods/Harmony/About/About.xml", "<ModMetaData><packageId>Brrainz.Harmony</packageId><forceLoadBefore><li>Ludeon.RimWorld</li></forceLoadBefore></ModMetaData>");

        var text = RimWorldShaped(CoreSource()).Render(Game, null, ["Harmony"]);

        OrderOf(text).Should().Equal("brrainz.harmony", "ludeon.rimworld");
    }

    [Fact]
    public void AWorkshopHarmonyAboveCoreStaysAboveCore()
    {
        WriteCore();
        var workshop = new FixedSource(ExternalListing.WhenListed, "brrainz.harmony");

        var text = RimWorldShaped(CoreSource(), workshop).Render(Game, Xml.Render(["brrainz.harmony", "ludeon.rimworld"], null), []);

        OrderOf(text).Should().Equal("brrainz.harmony", "ludeon.rimworld");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
