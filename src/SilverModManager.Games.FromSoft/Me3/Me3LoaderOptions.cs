namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>What a game says about how me3 loads it. The modded save name is the game definition's, not repeated here.</summary>
/// <param name="GameName">The game as me3 spells it, such as <c>eldenring</c>.</param>
/// <param name="OverlayNatives">The natives the game's overlay can hold.</param>
public sealed record Me3LoaderOptions(string GameName, IReadOnlyList<Me3OverlayNative> OverlayNatives);
