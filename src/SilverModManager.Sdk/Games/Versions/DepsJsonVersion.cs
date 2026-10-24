using System.Text.Json;
using DynamicData.Kernel;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games.Versions;

/// <summary>
/// Reads an application's own version out of its <c>.deps.json</c>.
/// </summary>
public static class DepsJsonVersion
{
    /// <summary>
    /// Unversioned projects publish this, so it says nothing about the real version.
    /// </summary>
    private static readonly Version UnversionedProject = new(1, 0, 0);

    /// <summary>
    /// Returns the version of the library with <c>"type": "project"</c> (key <c>Name/Version</c>), or none when the
    /// file is missing, unreadable, has no such library, or only the placeholder <c>1.0.0</c>.
    /// </summary>
    public static Optional<Version> TryRead(AbsolutePath depsJson)
    {
        try
        {
            if (!depsJson.FileExists) return Optional<Version>.None;

            using var stream = depsJson.Read();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Optional<Version>.None;
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
                return Optional<Version>.None;

            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object) continue;
                if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "project") continue;

                var slash = library.Name.LastIndexOf('/');
                if (slash < 0) continue;
                if (!VersionText.TryParse(library.Name.AsSpan(slash + 1), out var version)) continue;
                return version == UnversionedProject ? Optional<Version>.None : version;
            }

            return Optional<Version>.None;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A damaged deps.json means the version is unknown, not that the game is broken.
            return Optional<Version>.None;
        }
    }
}
