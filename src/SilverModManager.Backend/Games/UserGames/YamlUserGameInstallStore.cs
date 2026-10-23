using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using YamlDotNet.RepresentationModel;

namespace SilverModManager.Backend.Games.UserGames;

/// <summary>
/// Keeps the user's game installs in <c>XDG_CONFIG_HOME/SilverModManager/installs.yaml</c>, a list of
/// <c>{game, path, platform?, prefix?, wine?}</c> entries someone can also edit by hand.
/// </summary>
/// <remarks>
/// The file is parsed as a YAML node tree rather than into records so that an entry this code can't
/// read survives an <see cref="Add"/>: the user fixes it, we don't silently drop it.
/// </remarks>
internal sealed class YamlUserGameInstallStore : IUserGameInstallStore
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<YamlUserGameInstallStore> _logger;
    private readonly IEnumerable<IGameData> _games;
    private readonly object _lock = new();

    // The names installs.yaml uses for which build of the game is in the folder.
    private static readonly (string Name, OSPlatform Platform)[] Platforms = [("windows", OSPlatform.Windows), ("linux", OSPlatform.Linux)];

    public YamlUserGameInstallStore(IFileSystem fileSystem, ILogger<YamlUserGameInstallStore> logger, IEnumerable<IGameData> games)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _games = games;
    }

    private AbsolutePath FilePath => _fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME)
        .Combine($"{ApplicationConstants.DataDirectoryName}/installs.yaml");

    public IReadOnlyList<UserGameInstall> GetAll()
    {
        lock (_lock)
        {
            var root = Load() ?? new YamlSequenceNode();
            var installs = new List<UserGameInstall>();
            foreach (var node in root.Children)
            {
                if (Parse(node, out var problem) is { } install) installs.Add(install);
                else _logger.LogError("Skipping an entry of {File}: {Problem}", FilePath, problem);
            }
            return installs;
        }
    }

    public void Add(UserGameInstall install)
    {
        lock (_lock)
        {
            // Writing over a file we couldn't read would throw away every install the user recorded in it.
            var root = Load() ?? throw new InvalidOperationException($"{FilePath} could not be read as a list of installs; fix it before adding another");
            var entry = new YamlMappingNode
            {
                { "game", install.Game.ToString() },
                { "path", install.Path },
                { "platform", PlatformName(install) },
            };
            if (install.Prefix is { } prefix) entry.Add("prefix", prefix);
            if (install.Wine is { } wine) entry.Add("wine", wine);
            root.Add(entry);

            var path = FilePath;
            path.Parent.CreateDirectory();
            using var writer = new StreamWriter(path.Create());
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        }
    }

    /// <summary>
    /// The file's top-level list. A missing or empty file is an empty list; a file that doesn't parse or
    /// holds anything other than a list is reported and gives <c>null</c>.
    /// </summary>
    private YamlSequenceNode? Load()
    {
        var path = FilePath;
        if (!path.FileExists) return new YamlSequenceNode();

        try
        {
            using var reader = new StreamReader(path.Read());
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count == 0) return new YamlSequenceNode();

            if (stream.Documents[0].RootNode is YamlSequenceNode sequence) return sequence;
            _logger.LogError("{File} must hold a list of installs, found {Kind}", path, stream.Documents[0].RootNode.NodeType);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            _logger.LogError(e, "Could not parse {File}", path);
        }

        return null;
    }

    private UserGameInstall? Parse(YamlNode node, out string problem)
    {
        problem = "";

        if (node is not YamlMappingNode map)
        {
            problem = "not a mapping";
            return null;
        }

        var game = Scalar(map, "game");
        var path = Scalar(map, "path");
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(path))
        {
            problem = "both 'game' and 'path' are required";
            return null;
        }

        var gameId = GameId.From(game);
        var platformName = Scalar(map, "platform")?.Trim().ToLowerInvariant() ?? DefaultPlatformName(gameId);
        var platform = Array.FindIndex(Platforms, entry => entry.Name == platformName);
        if (platform < 0)
        {
            problem = $"unknown platform '{platformName}', expected 'windows' or 'linux'";
            return null;
        }

        return new UserGameInstall(gameId, path, Platforms[platform].Platform, Scalar(map, "prefix"), Scalar(map, "wine"));
    }

    // An entry without a platform is the build the game would run as anyway: its native one when it has one,
    // otherwise the Windows build under Wine.
    private string DefaultPlatformName(GameId gameId)
        => _games.FirstOrDefault(data => data.GameId == gameId)?.HasNativeLinuxBuild == true ? "linux" : "windows";

    private static string? Scalar(YamlMappingNode map, string key)
        => map.Children.TryGetValue(key, out var node) && node is YamlScalarNode scalar ? scalar.Value : null;

    private static string PlatformName(UserGameInstall install)
    {
        var platform = Array.FindIndex(Platforms, entry => entry.Platform == install.Platform);
        if (platform < 0) throw new ArgumentOutOfRangeException(nameof(install), install.Platform, "Only windows and linux installs can be recorded");
        return Platforms[platform].Name;
    }
}
