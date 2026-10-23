using NexusMods.Paths;

namespace NexusMods.Wabbajack.Format;

/// <summary>
/// Turns a path as Wabbajack writes it (backslashes, possibly <c>..</c>) into a <see cref="RelativePath"/> that
/// cannot leave its root. Modlists are untrusted input, the same as archive entries.
/// </summary>
internal static class ModlistPath
{
    public static RelativePath Sanitize(string raw)
    {
        var parts = new List<string>();
        foreach (var part in raw.Split('\\', '/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    // Resolved lexically; a `..` at the root has nothing to remove and is dropped.
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }
        return RelativePath.FromUnsanitizedInput(string.Join('/', parts));
    }
}
