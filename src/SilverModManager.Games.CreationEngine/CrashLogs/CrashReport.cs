using System.Text.RegularExpressions;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// One line of a crash log's call stack. <see cref="Symbol"/> is the engine function name with Ghidra's address
/// suffixes removed; for a frame in a DLL it is the logger's text after the pipe, untouched.
/// </summary>
/// <param name="Kind"><c>P</c> for a probable frame, <c>S</c> for one found by scanning the stack.</param>
/// <param name="AddressLibraryId">
/// The nearest preceding Address Library id the log printed. It is not an id for the function itself: several
/// different functions can share one.
/// </param>
public sealed partial record CrashFrame(int Index, char Kind, string Module, string Offset, string? Instruction, string? Symbol, string? AddressLibraryId)
{
    public bool IsExecutable => Module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name for people. Ghidra's auto-names (<c>FUN</c>, <c>sub</c>, <c>Func12</c>) carry no meaning, so they read as
    /// "an unnamed function", with the class when there is one. A frame with no symbol, or in a DLL, is its
    /// module and offset.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Symbol is null || !IsExecutable) return $"{Module}+{Offset}";
            var split = Symbol.LastIndexOf("::", StringComparison.Ordinal);
            var function = split < 0 ? Symbol : Symbol[(split + 2)..];
            if (!AutoName().IsMatch(function)) return Symbol;
            return split < 0 ? "an unnamed engine function" : $"an unnamed function of {Symbol[..split]}";
        }
    }

    [GeneratedRegex(@"^(FUN|sub|Func\d+)$")]
    private static partial Regex AutoName();
}

/// <summary>An entry of POSSIBLE RELEVANT OBJECTS. <see cref="Parenthetical"/> is a file or a plugin; the extension says which.</summary>
public sealed record RelevantObject(string Location, string Type, string? Name, string? FormId, string? Parenthetical);

/// <summary>One <c>Key: value</c> line of an object's expansion. The value is trimmed and has its surrounding quotes removed.</summary>
public sealed record DetailLine(string Key, string Value);

/// <summary>
/// The expansion of one object under STACK: or REGISTERS:. <see cref="Own"/> is the object's block, <see cref="Parents"/>
/// everything from its first <c>Checking Parent</c> on. The evidence that names a mod is often in the parent block,
/// so the two stay together.
/// </summary>
public sealed record ObjectDetails(string Location, string Type, string? Name, string? Parenthetical, IReadOnlyList<DetailLine> Own, IReadOnlyList<DetailLine> Parents)
{
    public IEnumerable<DetailLine> All => Own.Concat(Parents);
}

public sealed record SksePlugin(string Name, string? Version);

/// <summary>What a crash log says. Every section is optional: the logger may have been cut off, or died before the plugin list existed.</summary>
public sealed record CrashReport
{
    public string? CrashTime { get; init; }
    public string? GameVersion { get; init; }
    public string? LoggerVersion { get; init; }
    public string? ExceptionCode { get; init; }
    public string? FaultModule { get; init; }
    public string? FaultOffset { get; init; }
    public string? FaultInstruction { get; init; }
    public TimeSpan? Uptime { get; init; }
    public string? ThreadRole { get; init; }
    public IReadOnlyList<CrashFrame> Frames { get; init; } = [];
    public IReadOnlyList<RelevantObject> RelevantObjects { get; init; } = [];
    public IReadOnlyList<ObjectDetails> ObjectDetails { get; init; } = [];
    public IReadOnlyList<string> RecentModelRequests { get; init; } = [];
    public IReadOnlyList<string> ResourcePaths { get; init; } = [];
    public IReadOnlyList<SksePlugin> SksePlugins { get; init; } = [];
    public IReadOnlyList<string> Plugins { get; init; } = [];

    /// <summary>The <c>Total:</c> field of the PLUGINS counts line; null when the section has no counts line.</summary>
    public int? PluginCount { get; init; }

    /// <summary>The distinct plugin names the logger could not find, from the MISSING MASTERS section.</summary>
    public IReadOnlyList<string> MissingMasters { get; init; } = [];

    public IReadOnlyList<string> GameState { get; init; } = [];
}
