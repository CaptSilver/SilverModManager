using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// A tool as the user has it: where its exe is, plus anything they set that overrides the definition.
/// </summary>
/// <param name="Exe">The program's path on the host.</param>
/// <param name="Arguments">
/// A line the user typed. It replaces the definition's arguments, and may use <c>{Variable}</c> placeholders.
/// </param>
/// <param name="RunVia">Forces how the program is started instead of the definition's choice.</param>
/// <param name="OutputGroupName">Names the group the output is kept in instead of the definition's name.</param>
public sealed record InstalledTool(AbsolutePath Exe, string? Arguments = null, ToolRunVia? RunVia = null, string? OutputGroupName = null);
