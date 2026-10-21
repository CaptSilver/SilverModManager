using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.Declarative;
using NexusMods.Games.Declarative.Capabilities;
using NexusMods.Games.Declarative.Schema;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Declarative.Catalog;

/// <summary>A definition's YAML text and the name to report it under, a file path or resource name.</summary>
public readonly record struct DefinitionSource(string Name, string Text);

/// <summary>A definition that passed validation and the uniqueness checks.</summary>
public sealed record LoadedDefinition(GameDefinition Definition, string Source, bool IsBundled);

/// <summary>
/// Every game defined in YAML: the definitions embedded in this assembly, then the user's from
/// <c>XDG_CONFIG_HOME/SilverModManager/games/*.yaml</c> when the host asks for them.
/// </summary>
/// <remarks>
/// Loading happens in the constructor and is meant to run before the container exists, so it checks a
/// definition against the other definitions only. Coded games don't exist as instances yet; a bundled
/// definition clashing with one is caught by a test, and a user definition by the locators' own
/// handling of a repeated store id. A definition that fails any check is logged and skipped, it never
/// stops the others from loading.
/// </remarks>
public sealed partial class GameDefinitionCatalog : IGameDefinitionCatalog
{
    private const string ResourcePrefix = "games/";
    private const string UserDefinitionsFolder = "games";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<GameDefinitionCatalog> _logger;
    private readonly DefinitionValidator _validator;
    private readonly bool _loadUserDefinitions;
    private readonly Func<IEnumerable<IGameData>> _codedGames;
    private readonly object _lock = new();
    private ImmutableArray<LoadedDefinition> _definitions = [];

    /// <param name="fileSystem">Where the user's definitions are read from and written to.</param>
    /// <param name="capabilities">The capabilities a definition may name.</param>
    /// <param name="loadUserDefinitions">Whether to read the user's definitions. Only the app host passes true, so a test never reads the real config folder.</param>
    /// <param name="additionalBundled">Definitions loaded as if embedded, for tests.</param>
    /// <param name="codedGames">The coded games, asked for only when a folder is matched, because they are services that can't be resolved while the catalog loads.</param>
    public GameDefinitionCatalog(
        IFileSystem fileSystem,
        ICapabilityRegistry capabilities,
        ILogger<GameDefinitionCatalog> logger,
        bool loadUserDefinitions,
        IEnumerable<DefinitionSource>? additionalBundled = null,
        Func<IEnumerable<IGameData>>? codedGames = null)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _validator = new DefinitionValidator(capabilities);
        _loadUserDefinitions = loadUserDefinitions;
        _codedGames = codedGames ?? (() => []);

        var loaded = new List<LoadedDefinition>();
        foreach (var source in EmbeddedSources().Concat(additionalBundled ?? [])) TryAdd(loaded, source, isBundled: true);
        if (loadUserDefinitions)
        {
            foreach (var source in UserSources()) TryAdd(loaded, source, isBundled: false);
        }
        _definitions = [..loaded];
    }

    public ImmutableArray<LoadedDefinition> Definitions => _definitions;

    public bool TryGet(GameId gameId, [NotNullWhen(true)] out LoadedDefinition? definition)
    {
        foreach (var candidate in _definitions)
        {
            if (candidate.Definition.GetGameId() != gameId) continue;
            definition = candidate;
            return true;
        }

        definition = null;
        return false;
    }

    private AbsolutePath UserDefinitionsPath => _fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME)
        .Combine($"{ApplicationConstants.DataDirectoryName}/{UserDefinitionsFolder}");

    private static IEnumerable<DefinitionSource> EmbeddedSources()
    {
        var assembly = typeof(GameDefinitionCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".yaml", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Resource {name} is listed by the assembly but cannot be opened");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            yield return new DefinitionSource(name, reader.ReadToEnd());
        }
    }

    private IEnumerable<DefinitionSource> UserSources()
    {
        var folder = UserDefinitionsPath;
        List<AbsolutePath> files;
        try
        {
            if (!folder.DirectoryExists()) return [];
            files = folder.EnumerateFiles("*.yaml").OrderBy(file => file.FileName, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Skipping the user game definitions in {Folder}, it could not be read", folder);
            return [];
        }

        return files.Select(ReadUserFile).OfType<DefinitionSource>();
    }

    private DefinitionSource? ReadUserFile(AbsolutePath file)
    {
        try
        {
            using var reader = new StreamReader(file.Read(), Encoding.UTF8);
            return new DefinitionSource(file.ToString(), reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(e, "Skipping game definition {File}, it could not be read", file);
            return null;
        }
    }

    private void TryAdd(List<LoadedDefinition> loaded, DefinitionSource source, bool isBundled)
    {
        GameDefinition definition;
        try
        {
            definition = DefinitionSerializer.Read(source.Text);
        }
        catch (DefinitionException e)
        {
            _logger.LogError("Skipping game definition {Source}: {Problem}", source.Name, e.Message);
            return;
        }

        var problems = _validator.Validate(definition);
        if (problems.Count > 0)
        {
            _logger.LogError("Skipping game definition {Source}: {Problems}", source.Name, string.Join("; ", problems));
            return;
        }

        var identity = new GameIdentity(source.Name, definition.GetGameId(), definition.ToStoreIdentifiers());
        foreach (var other in loaded)
        {
            if (other.Definition.Id == definition.Id)
            {
                _logger.LogError("Skipping game definition {Source}: its id '{Id}' is already defined by {Other}", source.Name, definition.Id, other.Source);
                return;
            }

            var clashes = GameIdentityClashes.Find([identity, new GameIdentity(other.Source, other.Definition.GetGameId(), other.Definition.ToStoreIdentifiers())]);
            if (clashes.Count > 0)
            {
                _logger.LogError("Skipping game definition {Source}: it clashes with {Other}: {Clashes}", source.Name, other.Source, string.Join("; ", clashes));
                return;
            }
        }

        loaded.Add(new LoadedDefinition(definition, source.Name, isBundled));
    }

    /// <inheritdoc />
    public ImmutableArray<GameId> MatchFolder(AbsolutePath folder)
    {
        var matches = ImmutableArray.CreateBuilder<GameId>();
        foreach (var loaded in _definitions)
        {
            if (FileExistsIgnoringCase(folder, loaded.Definition.PrimaryExe)) matches.Add(loaded.Definition.GetGameId());
        }

        var definitionIds = _definitions.Select(loaded => loaded.Definition.GetGameId()).ToHashSet();
        foreach (var game in _codedGames())
        {
            if (definitionIds.Contains(game.GameId)) continue;
            if (CodedPrimaryFile(game, folder) is { } primary && FileExistsIgnoringCase(folder, primary)) matches.Add(game.GameId);
        }

        return matches.ToImmutable();
    }

    /// <summary>
    /// The path of a coded game's executable. <see cref="IGameData.GetPrimaryFile"/> wants an installation,
    /// which there isn't one of yet, so it is asked with a probe standing for a Windows build in
    /// <paramref name="folder"/>; a game whose answer needs real installation data (Bannerlord reads the
    /// store) throws and is not matched by folder.
    /// </summary>
    private string? CodedPrimaryFile(IGameData game, AbsolutePath folder)
    {
        try
        {
            var probe = new GameLocatorResult
            {
                Game = game,
                Path = folder,
                Store = GameStore.Unknown,
                StoreIdentifier = "",
                LocatorIds = [],
                Locator = FolderProbe.Instance,
                Platform = OSPlatform.Windows,
            };
            var installation = new GameInstallation(probe, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
            var primaryFile = game.GetPrimaryFile(installation);
            // A file in another location (a save folder, a prefix path) says nothing about the install folder.
            if (primaryFile.LocationId == LocationId.Game) return primaryFile.Path.ToString();

            _logger.LogDebug("{Game} launches a file outside its game folder and is not matched by folder", game.DisplayName);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "{Game} has no primary file without a located installation", game.DisplayName);
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="relative"/> is a file under <paramref name="folder"/>. Windows games are
    /// often on a case-sensitive file system with a differently-cased name than the definition's.
    /// </summary>
    private static bool FileExistsIgnoringCase(AbsolutePath folder, string relative)
    {
        var current = folder;
        var segments = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            try
            {
                var exact = current.Combine(segments[i]);
                if (last ? exact.FileExists : exact.DirectoryExists())
                {
                    current = exact;
                    continue;
                }

                var entries = last ? current.EnumerateFiles() : current.EnumerateDirectories();
                var segment = segments[i];
                var found = entries.Where(entry => string.Equals(entry.FileName, segment, StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
                if (found.Length == 0) return false;
                current = found[0];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unplugged drive or an unreadable folder is "not there", not a failure of the whole match.
                return false;
            }
        }

        return segments.Length > 0;
    }

    /// <inheritdoc />
    public GameId WriteMinimalUserDefinition(string name, RelativePath primaryExe)
    {
        if (!_loadUserDefinitions) throw new InvalidOperationException("This host doesn't use user game definitions");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A game needs a name", nameof(name));

        lock (_lock)
        {
            var folder = UserDefinitionsPath;
            var taken = _definitions.Select(loaded => loaded.Definition.Id)
                .Concat(_codedGames().Select(game => game.GameId.ToString()))
                .ToHashSet(StringComparer.Ordinal);

            var slug = Slug(name);
            var id = slug;
            for (var n = 2; taken.Contains(id) || folder.Combine($"{id}.yaml").FileExists; n++) id = $"{slug}-{n}";

            var definition = new GameDefinition
            {
                Id = id,
                Name = name.Trim(),
                Stores = new StoreDefinitions { ManualOnly = true },
                PrimaryExe = primaryExe.ToString().Replace('\\', '/'),
                Locations = new Dictionary<string, LocationDefinition> { [DefinitionValidator.GameLocation] = new() { Root = LocationRoots.Install } },
            };

            var problems = _validator.Validate(definition);
            if (problems.Count > 0) throw new ArgumentException($"Cannot write a definition for '{name}' with primary file '{primaryExe}': {string.Join("; ", problems)}");

            var file = folder.Combine($"{id}.yaml");
            var text = DefinitionSerializer.Write(definition);
            folder.CreateDirectory();
            using (var writer = new StreamWriter(file.Create(), new UTF8Encoding(false))) writer.Write(text);

            _definitions = _definitions.Add(new LoadedDefinition(definition, file.ToString(), IsBundled: false));
            return GameId.From(id);
        }
    }

    private static string Slug(string name)
    {
        var slug = NonSlugCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "game" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    /// <summary>The locator named by the probe installation in <see cref="CodedPrimaryFile"/>; it finds nothing by itself.</summary>
    private sealed class FolderProbe : IGameLocator
    {
        public static readonly FolderProbe Instance = new();

        public IEnumerable<GameLocatorResult> Locate() => throw new NotSupportedException("A folder probe only stands for a folder being matched; it does not locate games");
    }
}
