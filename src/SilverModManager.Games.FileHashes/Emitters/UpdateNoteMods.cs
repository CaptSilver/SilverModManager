using SilverModManager.Sdk.Errors;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// The installed mods an update note names: those with files that hook the game's code. Shared by the notes
/// that react to an update so they agree on which mods are at risk and how the list reads.
/// </summary>
internal static class UpdateNoteMods
{
    /// <summary>The mods that hook the game's code, or none when the game can't say which files do.</summary>
    public static IReadOnlyList<NativeCodeMod> Find(GameUpdateContext context)
        => context.Installation.Game is IGameNativeCode game ? NativeCodeMods.Find(context.Loadout, game) : [];

    /// <summary>One mod per line, as a Markdown list.</summary>
    public static MarkdownFragment List(IReadOnlyList<NativeCodeMod> mods) => new(string.Join("\n", mods.Select(mod => $"- {MarkdownText.Inline(mod.Name)}")));
}
