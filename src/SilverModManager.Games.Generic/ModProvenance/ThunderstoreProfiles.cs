using System.Collections.Immutable;
using System.Text.Json;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>
/// Reads the profiles r2modman and Gale keep per game: <c>&lt;root&gt;/&lt;game&gt;/profiles/&lt;profile&gt;/mods.yml</c> (or a
/// <c>mods.json</c>), a list of Thunderstore packages (<c>Owner-Name</c>) with their versions. Gale's profile layout is
/// not documented; this accepts the same shapes under its root and skips what it cannot read.
/// </summary>
internal static class ThunderstoreProfiles
{
    private const long MaxFileBytes = 2_000_000;

    public static void Read(string root, string origin, GameIdentity game, ImmutableArray<ModClaim>.Builder claims, ImmutableArray<string>.Builder problems)
    {
        var wanted = game.Names.Select(Names.Squash).Where(n => n.Length > 0).ToHashSet();
        foreach (var gameFolder in ManagerDiscovery.Children(root).Where(d => wanted.Contains(Names.Squash(Path.GetFileName(d)))))
        {
            foreach (var profile in ManagerDiscovery.Children(Path.Combine(gameFolder, "profiles")))
            {
                foreach (var file in new[] { "mods.yml", "mods.json" }.Select(f => Path.Combine(profile, f)).Where(File.Exists))
                    ReadFile(file, origin, claims, problems);
            }
        }
    }

    private static void ReadFile(string file, string origin, ImmutableArray<ModClaim>.Builder claims, ImmutableArray<string>.Builder problems)
    {
        string text;
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                problems.Add($"{file} is too large to be a profile and was not read.");
                return;
            }

            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{file} could not be read: {e.Message}");
            return;
        }

        var packages = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? FromJson(text) : FromYaml(text);
        if (packages.Count == 0 && text.Trim() is not ("" or "[]")) problems.Add($"No mods could be read from {file}.");

        foreach (var (name, version) in packages)
            claims.Add(new ModClaim(name, ClaimMethod.ManagerRecords, origin, ClaimSource.Thunderstore, Version: version, Package: name));
    }

    // r2modman's mods.yml is a list of maps. A small line reader is enough and keeps a YAML library out of this project.
    internal static List<(string Name, string? Version)> FromYaml(string text)
    {
        var list = new List<(string, string?)>();
        string? name = null, version = null, major = null, minor = null, patch = null;
        var inVersion = false;

        void Flush()
        {
            if (name is not null) list.Add((name, version ?? (major is not null && minor is not null && patch is not null ? $"{major}.{minor}.{patch}" : null)));
            name = version = major = minor = patch = null;
            inVersion = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            var newItem = trimmed.StartsWith("- ", StringComparison.Ordinal);
            if (newItem)
            {
                Flush();
                trimmed = trimmed[2..].TrimStart();
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;

            var key = trimmed[..colon].Trim();
            var value = Unquote(trimmed[(colon + 1)..].Trim());
            switch (key)
            {
                case "name": name = value.Length > 0 ? value : null; inVersion = false; break;
                case "versionNumber": version = value.Length > 0 ? value : null; inVersion = false; break;
                case "version": inVersion = value.Length == 0; if (!inVersion) version = value; break;
                case "major" when inVersion: major = value; break;
                case "minor" when inVersion: minor = value; break;
                case "patch" when inVersion: patch = value; break;
                default: inVersion = false; break;
            }
        }

        Flush();
        return list;
    }

    internal static List<(string Name, string? Version)> FromJson(string text)
    {
        var list = new List<(string, string?)>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var items = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement,
                JsonValueKind.Object when document.RootElement.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array => mods,
                _ => default,
            };
            if (items.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var name = new[] { "name", "fullName", "full_name" }.Select(k => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                if (name is null) continue;

                var versionElement = item.TryGetProperty("version", out var v1) ? v1 : item.TryGetProperty("versionNumber", out var v2) ? v2 : default;
                list.Add((name, versionElement.ValueKind switch
                {
                    JsonValueKind.String => versionElement.GetString(),
                    JsonValueKind.Object when Part(versionElement, "major") is { } a && Part(versionElement, "minor") is { } b && Part(versionElement, "patch") is { } c => $"{a}.{b}.{c}",
                    _ => null,
                }));
            }
        }
        catch (JsonException)
        {
            list.Clear();
        }

        return list;
    }

    private static long? Part(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'' ? value[1..^1] : value;
}
