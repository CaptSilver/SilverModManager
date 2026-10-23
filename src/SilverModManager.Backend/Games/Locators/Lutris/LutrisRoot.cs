using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Backend.Games.Locators;

/// <summary>
/// One row of Lutris's <c>games</c> table, limited to the columns the locator uses.
/// </summary>
/// <param name="Directory">The install directory Lutris recorded, or null when the row has none.</param>
/// <param name="Service">The store service the game was synced from (<c>gog</c>, <c>egs</c>), or null for a game added by hand.</param>
internal sealed record LutrisRow(string Slug, string? Runner, string? Directory, string ConfigPath, string? Service, string? ServiceId);

/// <summary>
/// The parts of a game's <c>games/&lt;configpath&gt;.yml</c> the locator uses.
/// </summary>
/// <param name="Exe">The executable as Lutris stores it: a host path, or a Windows path for a Wine game.</param>
/// <param name="Prefix">The Wine prefix, as written (a host path).</param>
/// <param name="WorkingDir">The game's working directory, as written.</param>
/// <param name="WineVersion">The name of the Lutris-managed Wine build under <c>runners/wine</c>, or null when the game uses the system Wine.</param>
internal sealed record LutrisGameConfig(string? Exe, string? Prefix, string? WorkingDir, string? WineVersion);

/// <summary>
/// One Lutris installation: <c>pga.db</c> and the runners live under the data folder, the per-game YAML files
/// under the config folder. A machine can have the native install and the Flatpak, each with its own games.
/// </summary>
internal sealed record LutrisRoot(AbsolutePath DataDir, AbsolutePath ConfigDir, bool IsFlatpak)
{
    private const string FlatpakApp = ".var/app/net.lutris.Lutris";

    public static ImmutableArray<LutrisRoot> All(IFileSystem fileSystem)
    {
        var home = fileSystem.GetKnownPath(KnownPath.HomeDirectory);
        return
        [
            new LutrisRoot(fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("lutris"), fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("lutris"), IsFlatpak: false),
            new LutrisRoot(home.Combine($"{FlatpakApp}/data/lutris"), home.Combine($"{FlatpakApp}/config/lutris"), IsFlatpak: true),
        ];
    }

    /// <summary>
    /// The wine binary of a Lutris-managed Wine build; it exists only once that build is downloaded.
    /// </summary>
    public AbsolutePath WineBinary(string version) => DataDir.Combine($"runners/wine/{version}/bin/wine");

    /// <summary>
    /// The games Lutris marks as installed. Empty when there is no <c>pga.db</c>, and empty with a warning
    /// when it cannot be read, so one broken install does not hide the other's games.
    /// </summary>
    public ImmutableArray<LutrisRow> InstalledGames(ILogger logger)
    {
        // SQLite opens the file itself, so the database is always a host path whatever IFileSystem is in use,
        // and a missing file has to be told apart from an unreadable one here.
        var database = DataDir.Combine("pga.db").GetFullPath();
        if (!File.Exists(database)) return [];

        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT slug, runner, directory, configpath, service, service_id FROM games WHERE installed = 1";
            using var reader = command.ExecuteReader();

            var games = ImmutableArray.CreateBuilder<LutrisRow>();
            while (reader.Read())
            {
                var slug = Text(reader, 0);
                var configPath = Text(reader, 3);
                if (slug is null || configPath is null) continue;
                games.Add(new LutrisRow(slug, Text(reader, 1), Text(reader, 2), configPath, Text(reader, 4), Text(reader, 5)));
            }

            return games.ToImmutable();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Lutris's games database at {Path}", database);
            return [];
        }
    }

    /// <summary>
    /// Reads a game's YAML; null with a warning when the file is missing or is not the YAML Lutris writes.
    /// </summary>
    public LutrisGameConfig? ReadGameConfig(LutrisRow row, ILogger logger)
    {
        var file = ConfigDir.Combine($"games/{row.ConfigPath}.yml");
        if (!file.FileExists)
        {
            logger.LogWarning("Lutris game {Slug} has no settings file at {Path}; skipping it", row.Slug, file);
            return null;
        }

        try
        {
            using var stream = file.Read();
            using var reader = new StreamReader(stream);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) return new LutrisGameConfig(null, null, null, null);

            var game = Child(root, "game");
            var wine = Child(root, "wine");
            return new LutrisGameConfig(Scalar(game, "exe"), Scalar(game, "prefix"), Scalar(game, "working_dir"), Scalar(wine, "version"));
        }
        catch (Exception e) when (e is YamlException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Skipping Lutris game {Slug}: cannot read its settings file {Path}", row.Slug, file);
            return null;
        }
    }

    private static string? Text(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column)) return null;
        var value = reader.GetValue(column).ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static YamlMappingNode? Child(YamlMappingNode? parent, string key) =>
        parent is not null && parent.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node as YamlMappingNode : null;

    private static string? Scalar(YamlMappingNode? parent, string key)
    {
        if (parent is null || !parent.Children.TryGetValue(new YamlScalarNode(key), out var node)) return null;
        var value = (node as YamlScalarNode)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
