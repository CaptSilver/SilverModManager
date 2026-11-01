using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// Everything about running one kind of modding tool, as data: which games it applies to, how it is found, what
/// it is given, what its Wine prefix needs and where its output goes. One generic runner reads it; there is no
/// class per tool.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>The catalogue id, or null for a program the user added by hand.</summary>
    public ToolId? Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Whether the tool works with a game. A predicate because a family covers a whole interface
    /// (every Creation Engine game for xEdit), not a list.
    /// </summary>
    public required Func<IGame, bool> AppliesTo { get; init; }

    /// <summary>File names to look for when scanning a folder. A function because xEdit's names vary by game.</summary>
    public required Func<IGame, IReadOnlyList<string>> ExecutableNames { get; init; }

    public required ArgumentTemplate Arguments { get; init; }

    /// <summary>
    /// Words added after <see cref="Arguments"/>, given the game and the exe's file name. xEdit's game switch is
    /// one: it depends on both.
    /// </summary>
    public Func<IGame, string, IReadOnlyList<string>>? ExtraArguments { get; init; }

    public ToolPrefixRequirements Prefix { get; init; } = ToolPrefixRequirements.None;

    public ToolOutput Output { get; init; } = ToolOutput.None;

    public ToolRunVia RunVia { get; init; } = ToolRunVia.Auto;

    public ExitCodePolicy ExitCodes { get; init; } = ExitCodePolicy.ZeroIsSuccess;

    /// <summary>
    /// Globs for output the loadout never keeps: backups of cleaned plugins, logs, caches, the tool's own config.
    /// </summary>
    public IReadOnlyList<string> IgnoredOutput { get; init; } = [];
}
