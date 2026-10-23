using System.Collections.Immutable;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// A stubbed game whose synchronizer recognises the content a test tells it about. The record paths filter the
/// way a Creation Engine game filters its catalog and manifests, so a record file is only indexed while an
/// earlier sync already tracked it.
/// </summary>
public class GameInstalledStubbedGame : IGame, IGameData<GameInstalledStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("GameInstalledStubbedGame");
    public static string DisplayName => "Stubbed Game With Game-Installed Content";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 5);

    /// <summary>The recognizer and the other statics are shared, so test classes using this game run one at a time.</summary>
    public const string CollectionName = "GameInstalledStubbedGame";

    public static FakeGameInstalledRecognizer Recognizer { get; private set; } = new();

    /// <summary>Paths whose bytes the synchronizer is told not to back up.</summary>
    public static HashSet<GamePath> IgnoredBackups { get; private set; } = [];

    /// <summary>What <c>WriteGameInstalledItemExtras</c> was handed.</summary>
    public static List<ExtrasCall> ExtrasCalls { get; private set; } = [];

    public record ExtrasCall(string SourceId, object? Extra, bool ItemCommittedAlready);

    public static void Reset()
    {
        Recognizer = new FakeGameInstalledRecognizer();
        IgnoredBackups = [];
        ExtrasCalls = [];
    }

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [48u],
        GOGProductIds = [48L],
        EGSCatalogItemId = ["epic-game-id-game-installed"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public GameInstalledStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new GameInstalledSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    public sealed class GameInstalledSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        protected override IGameInstalledContentRecognizer? GameInstalledContent => Recognizer;

        protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(path =>
            Recognizer.RecordPaths.Any(record => path == record || path.InFolder(record)));

        public override bool IsIgnoredBackupPath(GamePath path) => IgnoredBackups.Contains(path);

        protected override void WriteGameInstalledItemExtras(ITransaction tx, EntityId itemId, GameInstalledContent content)
        {
            var committed = GameInstalledItem.All(Connection.Db).Any(item => item.SourceId == content.SourceId);
            ExtrasCalls.Add(new ExtrasCall(content.SourceId, content.Extra, committed));
        }

        /// <summary>The reconcile on its own, so a test can look at what it commits.</summary>
        public ValueTask<Loadout.ReadOnly> Reconcile(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly metadata)
            => ReconcileGameInstalledContent(loadout, metadata);
    }
}

/// <summary>
/// A recognizer a test drives: the items it is given are reported while at least one of their files is in the
/// disk state, the way a real recognizer reports content.
/// </summary>
public sealed class FakeGameInstalledRecognizer : IGameInstalledContentRecognizer
{
    public List<GamePath> Record { get; } = [];
    public List<GameInstalledContent> Items { get; } = [];
    public bool Readable { get; set; } = true;
    public int Calls { get; private set; }

    public IReadOnlyList<GamePath> RecordPaths => Record;

    public ValueTask<GameInstalledRecognition> Recognize(GameInstallation installation, IReadOnlyDictionary<GamePath, SyncNodePart> diskState, CancellationToken token)
    {
        Calls++;
        if (!Readable) return ValueTask.FromResult(new GameInstalledRecognition([], RecordsReadable: false));

        var found = Items
            .Select(item => item with { Files = item.Files.Where(diskState.ContainsKey).ToArray() })
            .Where(item => item.Files.Count > 0)
            .ToArray();
        return ValueTask.FromResult(new GameInstalledRecognition(found, RecordsReadable: true));
    }
}
