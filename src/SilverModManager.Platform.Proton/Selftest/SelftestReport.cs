using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Platform.Proton.Selftest;

/// <summary>How one selftest check ended.</summary>
public enum SelftestStatus
{
    /// <summary>The check found what it looks for.</summary>
    Passed,

    /// <summary>A required check did not find it.</summary>
    Failed,

    /// <summary>An optional thing is missing. Reported, but does not fail the selftest.</summary>
    Absent,
}

/// <param name="Name">What is checked.</param>
/// <param name="Status">How the check ended.</param>
/// <param name="Required">Whether the selftest fails when the check does not pass.</param>
/// <param name="Detail">What was found, or why the check failed.</param>
public sealed record SelftestCheck(string Name, SelftestStatus Status, bool Required, string Detail);

/// <summary>
/// The result of the selftest: one line per check.
/// </summary>
public sealed record SelftestReport(IReadOnlyList<SelftestCheck> Checks)
{
    /// <summary>
    /// The options the <c>--json</c> form is written with.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// True when no required check failed.
    /// </summary>
    public bool Passed => Checks.All(c => !c.Required || c.Status != SelftestStatus.Failed);

    /// <summary>
    /// 0 when every required check passed, 1 otherwise.
    /// </summary>
    [JsonIgnore]
    public int ExitCode => Passed ? 0 : 1;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public IReadOnlyList<string> ToLines()
    {
        var lines = Checks.Select(c => $"{Marker(c.Status)} {c.Name}: {c.Detail}").ToList();
        lines.Add(Passed ? "All required checks passed." : "Some required checks failed.");
        return lines;
    }

    private static string Marker(SelftestStatus status) => status switch
    {
        SelftestStatus.Passed => "[ ok ]",
        SelftestStatus.Failed => "[FAIL]",
        _ => "[ -- ]",
    };
}
