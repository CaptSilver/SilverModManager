using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.ForeignDeployments;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.DataModel.ForeignDeployments;

/// <summary>
/// Reads the records Vortex leaves in a game folder: <c>vortex.deployment.json</c> for the default mod type and one
/// <c>vortex.deployment.&lt;modType&gt;.json</c> per other mod type. Each lists the files Vortex deployed and the
/// source mod folder each came from.
/// </summary>
/// <remarks>
/// The record files and the <c>__folder_managed_by_vortex</c> markers Vortex drops into folders it deploys to are
/// ignored: the app neither indexes nor ingests them, and a switched-off mod never deletes them. They stay Vortex's.
/// The <c>targetPath</c> and <c>stagingPath</c> in a record are Windows paths, often from another machine, so they
/// only hint at where a mod type's files sit; every file is found by looking for it under the game folder.
/// </remarks>
public sealed class VortexDeploymentSource(ILogger<VortexDeploymentSource> logger) : IForeignDeploymentSource
{
    private const string ManifestPrefix = "vortex.deployment";
    private const string ManifestSuffix = ".json";
    private const string BackupSuffix = ".vortex_backup";
    private const string MarkerName = "__folder_managed_by_vortex";
    private const string UnknownSource = "Unknown Vortex mod";

    /// <inheritdoc />
    public string ManagerName => "Vortex";

    /// <inheritdoc />
    public bool IsRecordFile(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;

        var name = path.FileName.ToString();
        if (name.Equals(MarkerName, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Path.Parent == RelativePath.Empty && IsManifestName(name);
    }

    private static bool IsManifestName(string name)
    {
        if (!name.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = name[ManifestPrefix.Length..^ManifestSuffix.Length];
        return rest.Length == 0 || (rest[0] == '.' && rest.Length > 1);
    }

    /// <inheritdoc />
    public ForeignDeployment? Detect(GameInstallation installation) => Detect(installation.Locations[LocationId.Game].Path);

    /// <summary>Reads the records in <paramref name="gameRoot"/>.</summary>
    public ForeignDeployment? Detect(AbsolutePath gameRoot)
    {
        var root = gameRoot.ToString();
        if (!Directory.Exists(root)) return null;

        var manifests = Directory.EnumerateFiles(root, ManifestPrefix + "*", SearchOption.TopDirectoryOnly)
            .Where(file => IsManifestName(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (manifests.Length == 0) return null;

        var disk = new GameFolderLookup(root);
        var files = new Dictionary<string, List<GamePath>>(StringComparer.Ordinal);
        var claimed = new HashSet<GamePath>();
        var missing = new List<GamePath>();
        var replaced = new List<GamePath>();

        foreach (var manifest in manifests)
        {
            var entries = ReadManifest(manifest, out var targetPath);
            if (entries is null) continue;

            var manifestName = Path.GetFileName(manifest);
            var isDefaultType = manifestName.Equals(ManifestPrefix + ManifestSuffix, StringComparison.OrdinalIgnoreCase);
            var candidates = isDefaultType ? [root] : disk.CandidateRoots(targetPath);

            foreach (var entry in entries)
            {
                var relative = Segments(entry.Target, entry.RelPath, manifestName);
                var found = candidates.Select(candidate => disk.Find(candidate, relative)).FirstOrDefault(match => match is not null);
                if (found is null)
                {
                    var expected = Path.GetRelativePath(root, Path.Combine(candidates[0], Path.Combine(relative)));
                    var missingPath = new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(expected));
                    logger.LogInformation("Vortex lists {Path} for {Source}, but it is not on disk, so it is not adopted", missingPath.Path, entry.Source);
                    missing.Add(missingPath);
                    continue;
                }

                if (!disk.IsInsideGameFolder(found))
                {
                    logger.LogInformation("{Path} from {Source} is a link that leads outside the game folder, so it is not adopted", found, entry.Source);
                    continue;
                }

                var gamePath = new GamePath(LocationId.Game, RelativePath.FromUnsanitizedInput(Path.GetRelativePath(root, found)));
                if (File.Exists(found + BackupSuffix))
                {
                    // Vortex moved the game's own file aside before putting its mod's over it. Adopting the mod file would
                    // delete the game file when the mod is switched off, with no original to put back.
                    logger.LogInformation("{Path} from {Source} replaced a game file that Vortex kept as {Backup}, so it is not adopted", gamePath.Path, entry.Source, gamePath.Path + BackupSuffix);
                    if (!replaced.Contains(gamePath)) replaced.Add(gamePath);
                    continue;
                }

                if (!claimed.Add(gamePath))
                {
                    logger.LogWarning("{Path} is listed by more than one Vortex source, keeping the first", gamePath.Path);
                    continue;
                }

                if (!files.TryGetValue(entry.Source, out var list)) files[entry.Source] = list = [];
                list.Add(gamePath);
            }
        }

        var mods = files
            .Select(pair =>
            {
                var (display, id, version) = ParseSource(pair.Key);
                return new ForeignMod(pair.Key, display, id, version, pair.Value);
            })
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Source, StringComparer.Ordinal)
            .ToArray();
        return new ForeignDeployment(ManagerName, mods, missing, replaced);
    }

    private sealed record Entry(string RelPath, string Source, string Target);

    private List<Entry>? ReadManifest(string manifest, out string targetPath)
    {
        targetPath = "";
        try
        {
            using var stream = File.OpenRead(manifest);
            using var document = JsonDocument.Parse(stream);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                logger.LogWarning("{Manifest} is not a Vortex deployment record, ignoring it", manifest);
                return null;
            }

            if (json.TryGetProperty("targetPath", out var target) && target.ValueKind == JsonValueKind.String)
                targetPath = target.GetString() ?? "";

            var entries = new List<Entry>();
            foreach (var file in files.EnumerateArray())
            {
                var relPath = Text(file, "relPath");
                if (relPath.Length == 0) continue;
                var source = Text(file, "source");
                entries.Add(new Entry(relPath, source.Length == 0 ? UnknownSource : source, Text(file, "target")));
            }

            return entries;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not read {Manifest}, ignoring it", manifest);
            return null;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>
    /// The folder names of <c>target</c> followed by those of <c>relPath</c>. A path that is rooted, names a drive or
    /// climbs with <c>..</c> would leave the game folder, so it refuses the whole record and the game is not added.
    /// </summary>
    private static string[] Segments(string target, string relPath, string manifestName)
    {
        var segments = new List<string>();
        foreach (var path in new[] { target, relPath })
        {
            if (path.StartsWith('\\') || path.StartsWith('/')) throw Outside(path, manifestName);
            foreach (var segment in path.Split('\\', '/'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == ".." || segment.Contains(':')) throw Outside(path, manifestName);
                segments.Add(segment);
            }
        }

        if (segments.Count == 0) throw Outside(relPath, manifestName);
        return segments.ToArray();
    }

    private static ForeignDeploymentRefusedException Outside(string path, string manifestName)
        => new($"The game was not added: Vortex's record {manifestName} lists {path}, which is outside the game folder. Purge the deployment in Vortex or remove that record, then try again.");

    /// <summary>
    /// Splits a Vortex source folder name, <c>name-modId-version parts-unixTime</c>, into what the player should see and
    /// what the app can keep for later. Anything that does not have that shape, including <c>Bundled - ...</c> names
    /// for files a collection bundled, keeps the whole text as its name.
    /// </summary>
    /// <remarks>
    /// The mod id is the first all-digit part that follows a part not starting with a digit, and everything between it and
    /// the time is version text, whatever it starts with (<c>v1-2</c>, <c>beta-1</c>). A name that itself holds such a
    /// number after a dash (<c>Mod-2-Electric-77-1-time</c>) is read as if that were the id; Vortex gives nothing to tell them apart.
    /// </remarks>
    public static (string DisplayName, ModId? NexusModId, string? Version) ParseSource(string source)
    {
        var plain = (source, (ModId?)null, (string?)null);
        if (source.StartsWith("Bundled - ", StringComparison.OrdinalIgnoreCase)) return plain;

        var tokens = source.Split('-');
        if (tokens.Length < 3 || !IsUnixTime(tokens[^1])) return plain;

        var end = tokens.Length - 1;
        var start = Enumerable.Range(1, end - 1).FirstOrDefault(i => IsDigits(tokens[i]) && tokens[i - 1].Length > 0 && !char.IsAsciiDigit(tokens[i - 1][0]));
        if (start == 0) return plain;

        if (!uint.TryParse(tokens[start], out var id) || id == 0) return plain;

        var name = string.Join('-', tokens[..start]).Trim();
        if (name.Length == 0) return plain;

        var version = end - start > 1 ? string.Join('.', tokens[(start + 1)..end]) : null;
        return (name, ModId.From(id), version);
    }

    private static bool IsDigits(string token) => token.Length > 0 && token.All(char.IsAsciiDigit);

    private static bool IsUnixTime(string token) => token.Length is >= 9 and <= 13 && IsDigits(token);
}
