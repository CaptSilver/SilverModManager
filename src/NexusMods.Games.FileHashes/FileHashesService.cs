using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.EpicGameStore.Values;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.GOG.Values;
using NexusMods.Sdk.Settings;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Games.FileHashes.DTOs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.MnemonicDB.Storage.RocksDbBackend;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.NexusModsApi;
using BuildId = NexusMods.Abstractions.GOG.Values.BuildId;
using Connection = NexusMods.MnemonicDB.Connection;

namespace NexusMods.Games.FileHashes;

internal sealed class FileHashesService : IFileHashesService, IDisposable, IHostedService
{
    private const string DefaultLanguage = "en-US";

    /// <summary>The name SQL addresses the downloaded or embedded database by: <c>MDB_HashRelation(DbName=>'hashes')</c>.</summary>
    private const string HashesConnectionName = "hashes";
    
    private readonly ScopedAsyncLock _lock = new();
    private readonly FileHashesServiceSettings _settings;
    private readonly IFileSystem _fileSystem;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly Dictionary<AbsolutePath, ConnectedDb> _databases;

    /// <summary>
    /// Databases <see cref="SetCurrent"/> replaced: their connections are already disposed, their stores
    /// and backends wait for <see cref="Dispose"/>. Kept out of <see cref="_databases"/> so reopening
    /// the same path registers a new connection instead of returning the dead one.
    /// </summary>
    private readonly List<ConnectedDb> _supersededDatabases = [];
    private readonly IServiceProvider _provider;
    private readonly AbsolutePath _hashDatabaseLocation;

    /// <summary>
    /// The currently connected database (if any)
    /// </summary>
    private ConnectedDb? _currentDb;

    private readonly ILogger<FileHashesService> _logger;
    private readonly IQueryEngine _queryEngine;

    private readonly LocalHashesStore _local;

    /// <summary>
    /// An empty store registered as "hashes" while no downloaded or embedded database is connected, so
    /// SQL that names it still binds. Never <see cref="_currentDb"/>, never returned by <see cref="Current"/>
    /// and never listed in <see cref="Databases"/>; <see cref="SetCurrent"/> disposes it once a real
    /// database takes the name.
    /// </summary>
    private LocalHashesStore? _placeholder;

    private record ConnectedDb(IDb Db, Connection Connection, DatomStore Store, Backend Backend, DatabaseInfo DatabaseInfo);

    public FileHashesService(
        ILogger<FileHashesService> logger,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        HttpClient httpClient,
        JsonSerializerOptions jsonSerializerOptions,
        IServiceProvider provider)
    {
        _logger = logger;
        _httpClient = httpClient;
        _jsonSerializerOptions = jsonSerializerOptions;
        _fileSystem = fileSystem;
        _settings = settingsManager.Get<FileHashesServiceSettings>();
        _databases = new Dictionary<AbsolutePath, ConnectedDb>();
        _provider = provider;
        _queryEngine = provider.GetRequiredService<IQueryEngine>();

        _hashDatabaseLocation = _settings.HashDatabaseLocation.ToPath(_fileSystem);
        _hashDatabaseLocation.CreateDirectory();

        // Opened here rather than in StartAsync so `Current` and `Local` work before the hosted service has run.
        _local = new LocalHashesStore(provider, _queryEngine, _settings, _fileSystem);

        // Only when nothing is on disk: with a database there, opening it is what registers the name.
        if (!ExistingDBs().Any()) EnsureHashesNameResolves();
    }

    private void EnsureHashesNameResolves()
    {
        if (_currentDb is not null || _placeholder is not null) return;

        _logger.LogDebug("No game hashes database is connected, registering an empty one so queries over it still run");
        _placeholder = LocalHashesStore.OpenEmptyPlaceholder(_provider, _queryEngine, HashesConnectionName);
    }

    private ConnectedDb OpenDb(DatabaseInfo databaseInfo)
    {
        try
        {
            if (_databases.TryGetValue(databaseInfo.Path, out var existing))
                return existing;

            _logger.LogInformation("Opening hash database at {Path} for {Timestamp}", databaseInfo.Path, databaseInfo.CreationTime);
            var backend = new Backend(readOnly: true);
            var settings = new DatomStoreSettings
            {
                Path = databaseInfo.Path,
            };

            var store = new DatomStore(_provider.GetRequiredService<ILogger<DatomStore>>(), settings, backend);
            var connection = new Connection(_provider.GetRequiredService<ILogger<Connection>>(), store, _provider, [], readOnlyMode: true, prefix: HashesConnectionName, queryEngine: _queryEngine);
            var connectedDb = new ConnectedDb(connection.Db, connection, store, backend, databaseInfo);

            _databases[databaseInfo.Path] = connectedDb;
            return connectedDb;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening database at {Path}", databaseInfo.Path);
            throw;
        }
    }

    /// <summary>
    /// Makes <paramref name="next"/> the database lookups read. The query engine resolves a name to
    /// whichever connection it finds first and never replaces one, so the previous connection, or the
    /// placeholder when that is all there was, has to be disposed, but only after the new one is
    /// registered: a moment with none would fail every query by name. The previous database's store and
    /// backend stay open until the service is disposed, so a database a caller already holds stays readable.
    /// </summary>
    private void SetCurrent(ConnectedDb next)
    {
        var previous = _currentDb;
        _currentDb = next;

        // The real connection is registered by now, so the name never stops resolving.
        _placeholder?.Dispose();
        _placeholder = null;

        if (previous is null || ReferenceEquals(previous, next)) return;

        previous.Connection.Dispose();
        _databases.Remove(previous.DatabaseInfo.Path);
        _supersededDatabases.Add(previous);
    }

    internal record struct DatabaseInfo(AbsolutePath Path, DateTimeOffset CreationTime);

    private IEnumerable<DatabaseInfo> ExistingDBs()
    {
        return _hashDatabaseLocation
            .EnumerateDirectories(recursive: false)
            .Where(d => !d.FileName.EndsWith("_tmp"))
            .Select(path =>
            {
                // Format is "{guid}_{timestamp}"
                var parts = path.FileName.Split('_');
                if (parts.Length != 2 || !ulong.TryParse(parts[1], out var timestamp)) return Optional<DatabaseInfo>.None;
                return new DatabaseInfo(Path: path, CreationTime: DateTimeOffset.FromUnixTimeSeconds((long)timestamp));
            })
            .Where(static optional => optional.HasValue)
            .Select(static optional => optional.Value)
            .OrderByDescending(static databaseInfo => databaseInfo.CreationTime);
    }

    /// <inheritdoc />
    public async Task CheckForUpdate(bool forceUpdate = false)
    {
        // Only a caller asking for a refresh by hand overrides the setting. The same flag also means
        // "re-check now" to the internal callers, which must keep honouring it.
        await CheckForUpdateCore(forceUpdate, explicitRequest: forceUpdate);
    }

    /// <inheritdoc />
    public IEnumerable<VanityVersion> GetKnownVanityVersions(NexusModsGameId nexusModsGameId)
    {
        return GetVersionDefinitions(nexusModsGameId)
            .Select(v => VanityVersion.From(v.Name))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The versions of a game in every store, the downloaded database's first. Each definition stays bound
    /// to the store it came from, since the entity ids it holds mean nothing in the other one.
    /// </summary>
    private List<VersionDefinition.ReadOnly> GetVersionDefinitions(NexusModsGameId nexusModsGameId)
    {
        return DatabasesCentralFirst()
            .SelectMany(db => VersionDefinition.All(db))
            .Where(v => v.GameId == nexusModsGameId)
            .ToList();
    }

    /// <summary>
    /// The stores a lookup by version name reads: the downloaded or embedded database first, because its
    /// names are the readable ones, then the local store, which names a version after the store's own build id.
    /// </summary>
    private IEnumerable<IDb> DatabasesCentralFirst()
    {
        var central = _currentDb;
        if (central is not null) yield return central.Db;
        yield return Local;
    }

    private bool ShouldCheckForUpdate()
    {
        if (!GameHashesReleaseFileName.FileExists) return true;
        var lastUpdated = GameHashesReleaseFileName.FileInfo.LastWriteTimeUtc;
        var diff = DateTime.UtcNow - lastUpdated;
        return diff >= _settings.HashDatabaseUpdateInterval;
    }


    /// <param name="forceUpdate">Check for a newer release even when the last check is recent.</param>
    /// <param name="explicitRequest">The user asked for the download, so <see cref="FileHashesServiceSettings.SkipUpdateCheck"/> does not apply.</param>
    /// <param name="unreadable">A database that failed to open and must not be tried again.</param>
    private async Task CheckForUpdateCore(bool forceUpdate, CancellationToken cancellationToken = default, bool explicitRequest = false, AbsolutePath? unreadable = null)
    {
        using var _ = await _lock.LockAsync();

        try
        {
            await ConnectLatestDatabase(forceUpdate, explicitRequest, unreadable, cancellationToken);
        }
        finally
        {
            // Every early return below that connects nothing leaves "hashes" unregistered.
            EnsureHashesNameResolves();
        }
    }

    private async Task ConnectLatestDatabase(bool forceUpdate, bool explicitRequest, AbsolutePath? unreadable, CancellationToken cancellationToken)
    {
        DatabaseInfo[] Readable() => ExistingDBs().Where(info => info.Path != unreadable).ToArray();

        var existingDatabases = Readable();
        var downloadAllowed = explicitRequest || !_settings.SkipUpdateCheck;
        var shouldCheckForUpdate = downloadAllowed && (forceUpdate || existingDatabases.Length == 0 || ShouldCheckForUpdate());

        if (!shouldCheckForUpdate && existingDatabases.TryGetFirst(out var latestDatabase))
        {
            SetCurrent(OpenDb(latestDatabase));
            return;
        }

        Manifest? latestReleaseManifest = null;
        if (shouldCheckForUpdate)
        {
            latestReleaseManifest = await FetchLatestReleaseManifest(GameHashesReleaseFileName, cancellationToken: cancellationToken);
        }

        if (existingDatabases.Length == 0)
        {
            var embeddedDatabaseInfo = await AddEmbeddedDatabase(cancellationToken: cancellationToken);
            if (latestReleaseManifest is null)
            {
                if (!embeddedDatabaseInfo.HasValue)
                {
                    _logger.LogError("Failed to add embedded game hashes database and failed to fetch latest release manifest, game hashes functionality will be unavailable");
                    return;
                }

                if (!downloadAllowed) _logger.LogInformation("Central game hashes database download is disabled by settings, using the embedded database");
                else _logger.LogWarning("Failed to fetch latest release manifest, defaulting to embedded game hashes database which may be out-of-date");
                SetCurrent(OpenDb(embeddedDatabaseInfo.Value));
                return;
            }

            Debug.Assert(latestReleaseManifest is not null, "should've returned if we didn't have a manifest");
            if (embeddedDatabaseInfo.HasValue)
            {
                existingDatabases = Readable();
                Debug.Assert(existingDatabases.Length >= 1, $"should have at least one database but found {existingDatabases.Length}");
            }
        }

        if (latestReleaseManifest is null && existingDatabases.Length == 0)
        {
            _logger.LogError("Failed to fetch the latest release manifest and failed to use the embedded database, game hashes functionality will be unavailable");
            return;
        }

        if (latestReleaseManifest is null || existingDatabases[0].CreationTime.ToUnixTimeSeconds() >= latestReleaseManifest.CreatedAt.ToUnixTimeSeconds())
        {
            SetCurrent(OpenDb(existingDatabases[0]));
            return;
        }

        _logger.LogInformation("Fetching latest games hashes database");
        var releaseDatabaseInfo = await AddReleaseDatabase(latestReleaseManifest, cancellationToken);
        if (!releaseDatabaseInfo.HasValue)
        {
            // Without this, a first run (or a call that skipped StartAsync) leaves no database connected.
            _logger.LogWarning("Failed to add the latest game hashes database, using the existing one which may be out-of-date");
            SetCurrent(OpenDb(existingDatabases[0]));
            return;
        }

        SetCurrent(OpenDb(releaseDatabaseInfo.Value));
    }

    private async ValueTask<Manifest?> FetchLatestReleaseManifest(AbsolutePath storagePath, CancellationToken cancellationToken)
    {
        const int defaultTimeout = 15;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(delay: TimeSpan.FromSeconds(defaultTimeout));

        try
        {
            await using var fileStream = storagePath.Create();
            await using (var httpStream = await _httpClient.GetStreamAsync(_settings.GithubManifestUrl, cancellationToken: cts.Token))
            {
                await httpStream.CopyToAsync(fileStream, cancellationToken: cts.Token);
            }

            fileStream.Position = 0;
            return await JsonSerializer.DeserializeAsync<Manifest>(fileStream, _jsonSerializerOptions, cancellationToken: cts.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to fetch latest release manifest from `{Url}`", _settings.GithubManifestUrl);
            return null;
        }
    }

    private async ValueTask<Optional<DatabaseInfo>> AddEmbeddedDatabase(CancellationToken cancellationToken)
    {
        try
        {
            var streamFactory = new EmbeddedResourceStreamFactory<FileHashesService>(resourceName: "games_hashes_db.zip");
            await using var archiveStream = await streamFactory.GetStreamAsync();
            var creationTime = ApplicationConstants.IsDebug ? DateTimeOffset.UnixEpoch : ApplicationConstants.BuildDate;

            var path = await AddDatabase(archiveStream, creationTime, cancellationToken);
            return new DatabaseInfo(path, creationTime);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to add embedded database");
            return Optional<DatabaseInfo>.None;
        }
    }

    internal async ValueTask<Optional<DatabaseInfo>> AddReleaseDatabase(Manifest releaseManifest, CancellationToken cancellationToken)
    {
        try
        {
            // The manifest and the zip come from separate requests, so the manifest is the only
            // thing that says what the zip is supposed to contain.
            var asset = releaseManifest.Assets.FirstOrDefault(static asset => asset.Type == AssetType.GameHashDb)
                ?? throw new InvalidDataException("The release manifest does not list a game hashes database asset");

            await using var httpStream = await _httpClient.GetStreamAsync(_settings.GameHashesDbUrl, cancellationToken: cancellationToken);
            var path = await AddDatabase(httpStream, releaseManifest.CreatedAt, cancellationToken, expectedArchive: asset);
            return new DatabaseInfo(path, releaseManifest.CreatedAt);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to add release database from {Url}", _settings.GameHashesDbUrl);
            return Optional<DatabaseInfo>.None;
        }
    }

    private async ValueTask<AbsolutePath> AddDatabase(
        Stream archiveStream,
        DateTimeOffset databaseCreationTime,
        CancellationToken cancellationToken,
        Asset? expectedArchive = null)
    {
        var name = $"{Guid.NewGuid()}_{databaseCreationTime.ToUnixTimeSeconds()}";

        await using var archivePath = new TemporaryPath(_fileSystem, _hashDatabaseLocation / $"{name}.zip");
        await using (var fileStream = archivePath.Path.Create())
        {
            await archiveStream.CopyToAsync(fileStream, cancellationToken: cancellationToken);
        }

        if (expectedArchive is not null) await VerifyArchive(archivePath.Path, expectedArchive, cancellationToken);

        await using var extractionDirectory = new TemporaryPath(_fileSystem, _hashDatabaseLocation / $"{name}_tmp");
        await using (var fileStream = archivePath.Path.Read())
        using (var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Read))
        {
            // Resolve every entry before writing any, so a bad archive leaves nothing behind.
            var destinations = zipArchive.Entries
                .Select(entry => (Entry: entry, Destination: ResolveEntryPath(extractionDirectory.Path, entry.FullName)))
                .ToArray();

            foreach (var (fileEntry, destinationPath) in destinations)
            {
                destinationPath.Parent.CreateDirectory();

                await using var entryStream = fileEntry.Open();
                await using var outputStream = destinationPath.Create();
                await entryStream.CopyToAsync(outputStream, cancellationToken: cancellationToken);
            }
        }

        var finalDirectory = _hashDatabaseLocation / name;
        Directory.Move(
            sourceDirName: extractionDirectory.Path.ToNativeSeparators(OSInformation.Shared),
            destDirName: finalDirectory.ToNativeSeparators(OSInformation.Shared)
        );

        return finalDirectory;
    }

    /// <summary>
    /// Checks the downloaded archive against the size and xxHash3 the release manifest lists for it.
    /// The database builder computes the manifest hash the same way, as the xxHash3 of the whole zip file.
    /// </summary>
    private static async ValueTask VerifyArchive(AbsolutePath archivePath, Asset expected, CancellationToken cancellationToken)
    {
        var actualSize = archivePath.FileInfo.Size;
        if (actualSize != expected.Size)
            throw new InvalidDataException($"Downloaded archive is {actualSize} but the release manifest lists {expected.Size}");

        var actualHash = await archivePath.XxHash3Async(token: cancellationToken);
        if (actualHash != expected.Hash)
            throw new InvalidDataException($"Downloaded archive hash {actualHash} does not match the release manifest hash {expected.Hash}");
    }

    /// <summary>
    /// Maps a zip entry name to its destination below <paramref name="root"/>. Entry names are
    /// attacker-controlled, and <c>Combine</c> keeps <c>..</c> segments, so the result is
    /// normalised and has to stay under the root.
    /// </summary>
    private AbsolutePath ResolveEntryPath(AbsolutePath root, string entryName)
    {
        if (Path.IsPathRooted(entryName))
            throw new InvalidDataException($"Archive entry `{entryName}` has an absolute path");

        var rootPath = Path.GetFullPath(root.ToString());
        var fullPath = Path.GetFullPath(root.Combine(entryName).ToString());
        if (!fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry `{entryName}` resolves outside the extraction directory");

        return _fileSystem.FromUnsanitizedFullPath(fullPath);
    }

    private AbsolutePath GameHashesReleaseFileName => _hashDatabaseLocation / _settings.ReleaseFilePath;

    /// <inheritdoc />
    public async ValueTask<IDb> GetFileHashesDb()
    {
        if (_currentDb is not null)
            return _currentDb.Db;

        // Call core since we're already inside a lock
        await CheckForUpdateCore(false);

        return Current;
    }

    /// <inheritdoc/>
    public IEnumerable<GameFileRecord> GetGameFiles(LocatorIdsWithGameStore locatorIdsWithGameStore)
    {
        var (gameStore, locatorIds) = locatorIdsWithGameStore;

        if (gameStore == GameStore.GOG)
        {
            // Entity ids only identify a row inside their own store, so rows are keyed by store as well.
            Dictionary<(IConnection Store, EntityId Id), GogBuild.ReadOnly> gogBuilds = [];
            HashSet<ProductId> gogProducts = [];
            Dictionary<(IConnection Store, EntityId Id), GogManifest.ReadOnly> gogManifests = [];
            
            // So first we find all the valid build Ids, and then assume that everything else is a product Id
            foreach (var id in locatorIds)
            {
                if (!ulong.TryParse(id.Value, out var parsedId))
                    continue;

                var gogId = BuildId.From(parsedId);

                if (TryFindGogBuild(gogId, out var firstBuild))
                {
                    gogBuilds[(firstBuild.Db.Connection, firstBuild.Id)] = firstBuild;
                    continue;
                }
                
                var productId = ProductId.From(parsedId);
                gogProducts.Add(productId);
            }
            
            // Now we emit all the files from the build products, and then also from any secondary products
            foreach (var build in gogBuilds.Values)
            {
                foreach (var depot in build.Depots)
                {
                    // We only care about the productId of the build, and the productIds of the secondary products
                    if (!(depot.ProductId == build.ProductId || gogProducts.Contains(depot.ProductId)))
                        continue;
                    
                    // If there is a language setting for the files, they have to be the same as the default language
                    if (!(depot.Languages.Count == 0 || depot.Languages.Contains(DefaultLanguage)))
                        continue;

                    gogManifests[(depot.Manifest.Db.Connection, depot.Manifest.Id)] = depot.Manifest;
                }
            }

            foreach (var (_ , manifest) in gogManifests)
            {
                foreach (var file in manifest.Files)
                {
                    yield return new GameFileRecord
                    {
                        Path = (LocationId.Game, file.Path),
                        Size = file.Hash.Size,
                        MinimalHash = file.Hash.MinimalHash,
                        Hash = file.Hash.XxHash3,
                    };
                }
            }
            
        }
        else if (gameStore == GameStore.Steam)
        {
            foreach (var id in locatorIds)
            {
                if (!ulong.TryParse(id.Value, out var parsedId))
                    continue;
                
                var manifestId = ManifestId.From(parsedId);

                if (!TryFindSteamManifest(manifestId, out var firstManifest))
                    continue;

                foreach (var file in firstManifest.Files)
                {
                    yield return new GameFileRecord
                    {
                        Path = (LocationId.Game, file.Path),
                        Size = file.Hash.Size,
                        MinimalHash = file.Hash.MinimalHash,
                        Hash = file.Hash.XxHash3,
                    };
                }
            }
        }
        else if (gameStore == GameStore.EGS)
        {
            foreach (var manifestId in locatorIds)
            {
                var egManifestId = ManifestHash.FromUnsanitized(manifestId.Value);
                
                if (!TryFindEpicBuild(egManifestId, out var firstManifest))
                {
                    _logger.LogWarning("No EGS manifest found for {ManifestId}", egManifestId.Value);
                    continue;
                }
                
                foreach (var file in firstManifest.Files)
                {
                    yield return new GameFileRecord
                    {
                        Path = (LocationId.Game, file.Path),
                        Size = file.Hash.Size,
                        MinimalHash = file.Hash.MinimalHash,
                        Hash = file.Hash.XxHash3,
                    };
                }
            }
        }
        else
        {
            throw new NotSupportedException("No way to get game files for: " + gameStore);
        }
    }

    // File lookups ask the local store first, per id: it is written from the install's own data, so the
    // manifest it holds for an id describes the disk better than the downloaded one. An id only the
    // downloaded database knows is still answered by it.
    private bool TryFindSteamManifest(ManifestId manifestId, out SteamManifest.ReadOnly manifest)
    {
        foreach (var db in Databases)
        {
            if (SteamManifest.FindByManifestId(db, manifestId).TryGetFirst(out manifest)) return true;
        }

        manifest = default;
        return false;
    }

    private bool TryFindGogBuild(BuildId buildId, out GogBuild.ReadOnly build)
    {
        foreach (var db in Databases)
        {
            if (GogBuild.FindByBuildId(db, buildId).TryGetFirst(out build)) return true;
        }

        build = default;
        return false;
    }

    private bool TryFindEpicBuild(ManifestHash manifestHash, out EpicGameStoreBuild.ReadOnly build)
    {
        foreach (var db in Databases)
        {
            if (EpicGameStoreBuild.FindByManifestHash(db, manifestHash).TryGetFirst(out build)) return true;
        }

        build = default;
        return false;
    }

    /// <inheritdoc />
    public IDb Current => _currentDb?.Db ?? Local;

    /// <inheritdoc />
    public IDb Local => _local.Connection.Db;

    /// <inheritdoc />
    public IReadOnlyList<IDb> Databases => _currentDb is null ? [Local] : [Local, _currentDb.Db];

    /// <inheritdoc />
    public bool TryGetVanityVersion(LocatorIdsWithGameStore locatorIdsWithGameStore, out VanityVersion version)
    {
        if (TryGetGameVersionDefinition(locatorIdsWithGameStore, out var versionDefinition))
        {
            version = VanityVersion.From(versionDefinition.Name);
            return true;
        }

        version = VanityVersion.DefaultValue;
        return false;
    }

    private bool TryGetGameVersionDefinition(
        LocatorIdsWithGameStore locatorIdsWithGameStore,
        out VersionDefinition.ReadOnly versionDefinition)
    {
        // The downloaded database names versions after releases, the local store after the store's
        // build id, so the local one only answers when the downloaded one has nothing.
        foreach (var db in DatabasesCentralFirst())
        {
            if (TryGetGameVersionDefinition(db, locatorIdsWithGameStore, out versionDefinition)) return true;
        }

        versionDefinition = default;
        return false;
    }

    private bool TryGetGameVersionDefinition(
        IDb db,
        LocatorIdsWithGameStore locatorIdsWithGameStore,
        out VersionDefinition.ReadOnly versionDefinition)
    {
        var (gameStore, locatorIds) = locatorIdsWithGameStore;

        versionDefinition = default(VersionDefinition.ReadOnly);
        if (gameStore == GameStore.GOG)
        {
            List<GogBuild.ReadOnly> gogBuilds = [];

            foreach (var gogId in locatorIds)
            {
                if (!ulong.TryParse(gogId.Value, out var parsedId))
                {
                    _logger.LogWarning("Unable to parse `{Raw}` as ulong", gogId);
                    return false;
                }

                var hasBuild = GogBuild.FindByBuildId(db, BuildId.From(parsedId)).TryGetFirst(out var gogBuild);
                if (hasBuild) gogBuilds.Add(gogBuild);
            }

            if (gogBuilds.Count == 0)
            {
                _logger.LogDebug("No GOG builds found");
                return false;
            }

            var hasVersionDefinition = VersionDefinition.All(db)
                .Select(version =>
                {
                    var matchingIdCount = gogBuilds.Count(g => version.GogBuildsIds.Contains(g));
                    return (matchingIdCount, version);
                })
                .Where(row => row.matchingIdCount > 0)
                .OrderByDescending(row => row.matchingIdCount)
                .Select(t => t.version)
                .TryGetFirst(out versionDefinition);

            if (!hasVersionDefinition)
            {
                _logger.LogDebug("No matching version definition found");
                return false;
            }
        }
        else if (gameStore == GameStore.Steam)
        {
            List<SteamManifest.ReadOnly> steamManifests = [];
            
            foreach (var steamId in locatorIds)
            {
                if (!ulong.TryParse(steamId.Value, out var parsedId))
                {
                    _logger.LogDebug("Steam locator {Raw} metadata is not a valid ulong", steamId);
                    return false;
                }

                var hasManifest = SteamManifest.FindByManifestId(db, ManifestId.From(parsedId)).TryGetFirst(out var steamManifest);
                if (hasManifest) steamManifests.Add(steamManifest);
            }

            if (steamManifests.Count == 0)
            {
                _logger.LogDebug("No Steam manifests found for locator metadata");
                return false;
            }
            
            var wasFound = VersionDefinition.All(db)
                .Select(version =>
                {
                    var matchingIdCount = steamManifests.Count(g => version.SteamManifestsIds.Contains(g));
                    return (matchingIdCount, version);
                })
                .Where(row => row.matchingIdCount > 0)
                .OrderByDescending(row => row.matchingIdCount)
                .Select(t => t.version)
                .TryGetFirst(out versionDefinition);

            if (!wasFound)
            {
                _logger.LogDebug("No version found for locator metadata");
                return false;
            }
        }
        else if (gameStore == GameStore.EGS)
        {
            var versionsByManifestHash = VersionDefinition.All(db)
                .SelectMany(version =>
                    {
                        if (VersionDefinition.EpicGameStoreBuildsIds.IsIn(version)) 
                            return version.EpicGameStoreBuilds.Select(build => (Version: version, Build: build));
                        return [];
                    }
                )
                .ToLookup(row => row.Build.ManifestHash);

            var builds = locatorIds
                .Select(locatorString => ManifestHash.FromUnsanitized(locatorString.Value))
                .SelectMany(manifestHash => versionsByManifestHash[manifestHash].Select(row => (row.Version, manifestHash)));

            if (builds.TryGetFirst(out var build))
            {
                versionDefinition = build.Version;
                return true;
            }

            return false;
        }
        else
        {
            _logger.LogDebug("No way to get game version for: {Store}", gameStore);
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryGetLocatorIdsForVanityVersion(GameStore gameStore, VanityVersion version, out LocatorId[] commonIds)
    {
        foreach (var db in DatabasesCentralFirst())
        {
            if (!VersionDefinition.FindByName(db, version.Value).TryGetFirst(out var versionDef)) continue;

            commonIds = GetLocatorIdsForVersionDefinition(gameStore, versionDef);
            return true;
        }

        commonIds = [];
        return false;
    }

    public LocatorId[] GetLocatorIdsForVersionDefinition(GameStore gameStore, VersionDefinition.ReadOnly versionDefinition)
    {
        if (gameStore == GameStore.GOG)
        {
            return versionDefinition.GogBuilds.Select(build => LocatorId.From(build.BuildId!.Value.ToString())).ToArray();
        }

        if (gameStore == GameStore.Steam)
        {
            return versionDefinition.SteamManifests.Select(manifest => LocatorId.From(manifest.ManifestId.ToString())).ToArray();
        }
        
        if (gameStore == GameStore.EGS)
        {
            return versionDefinition.EpicGameStoreBuilds.Select(build => LocatorId.From(build.ManifestHash.Value)).ToArray();
        }

        throw new NotSupportedException("No way to get common IDs for: " + gameStore);
    }

    /// <inheritdoc />
    public Optional<VersionData> SuggestVersionData(GameInstallation gameInstallation, IEnumerable<(GamePath Path, Hash Hash)> files)
    {
        var filesSet = files.ToHashSet();

        List<(VersionData VersionData, int Matches)> versionMatches = [];
        foreach (var versionDefinition in GetVersionDefinitions(gameInstallation.Game.NexusModsGameId.Value))
        {
            var locatorIds = GetLocatorIdsForVersionDefinition(gameInstallation.LocatorResult.Store, versionDefinition);

            var matchingCount = GetGameFiles((gameInstallation.LocatorResult.Store, locatorIds))
                .Count(file => filesSet.Contains((file.Path, file.Hash)));

            versionMatches.Add((new VersionData(locatorIds, VanityVersion.From(versionDefinition.Name)), matchingCount));
        }

        return versionMatches
            .OrderByDescending(t => t.Matches)
            .Select(t => t.VersionData)
            .FirstOrOptional(_ => true);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var database in _databases.Values) database.Connection.Dispose();
        var allDatabases = _databases.Values.Concat(_supersededDatabases).ToArray();
        foreach (var database in allDatabases) database.Store.Dispose();
        foreach (var database in allDatabases) database.Backend.Dispose();
        _placeholder?.Dispose();
        _local.Dispose();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var existingDatabases = ExistingDBs().ToArray();

        // Cleanup old databases
        foreach (var databaseInfo in existingDatabases.Skip(1))
        {
            databaseInfo.Path.DeleteDirectory(true);
        }

        AbsolutePath? unreadable = null;
        try
        {
            if (existingDatabases.TryGetFirst(out var latestDatabase))
            {
                unreadable = latestDatabase.Path;
                SetCurrent(OpenDb(latestDatabase));
                unreadable = null;
            }
        }
        catch (Exception e)
        {
            // Recovery re-checks for a release when downloads are allowed; with them off it falls back to the embedded database.
            _logger.LogError(e, "Failed to open latest database, replacing it");
        }

        await CheckForUpdateCore(forceUpdate: unreadable is not null, cancellationToken, unreadable: unreadable);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
