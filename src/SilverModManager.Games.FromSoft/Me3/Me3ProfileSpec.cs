namespace SilverModManager.Games.FromSoft.Me3;

/// <summary>What differs between games in the me3 profile.</summary>
/// <param name="GameName">The game as me3 spells it, such as <c>eldenring</c>.</param>
/// <param name="ModdedSave">The save a modded session is pointed at; <c>null</c> writes no savefile line.</param>
/// <param name="OverlayNatives">The natives that can come from the overlay, in the order they are listed.</param>
public sealed record Me3ProfileSpec(string GameName, string? ModdedSave, IReadOnlyList<Me3OverlayNative> OverlayNatives);
