using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace SilverModManager.Library.Lock;

[PublicAPI]
public enum LockLevel
{
    Ok,
    Warn,
    Fail,
}

[PublicAPI]
public sealed record LockReportItem(LockLevel Level, string Message);

/// <summary>
/// The result of checking a lock against the library, in the shape erm's reports have: the worst level and one item
/// per finding. Only a failure makes the exit code non-zero; a warning is a thing to look at.
/// </summary>
[PublicAPI]
public sealed class LockReport(IReadOnlyList<LockReportItem> items)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IReadOnlyList<LockReportItem> Items { get; } = items;

    public LockLevel Worst => Items.Count == 0 ? LockLevel.Ok : Items.Max(item => item.Level);

    public int ExitCode => Worst == LockLevel.Fail ? 1 : 0;

    public string ToJson() => JsonSerializer.Serialize(new { Worst, Items }, JsonOptions);
}
