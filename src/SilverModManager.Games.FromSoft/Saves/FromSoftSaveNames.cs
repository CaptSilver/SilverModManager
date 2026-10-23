namespace SilverModManager.Games.FromSoft.Saves;

/// <summary>The save files of a game, by file name inside the account's save folder.</summary>
/// <param name="Vanilla">The save file the game writes.</param>
/// <param name="Modded">
/// The save a modded session is pointed at so it never opens <paramref name="Vanilla"/>; <c>null</c> when the game's
/// loader cannot redirect saves, in which case no profile writes a savefile line and the vanilla guard copies only
/// <paramref name="Vanilla"/>.
/// </param>
/// <param name="SeamlessCoop">The save Seamless Co-op writes; <c>null</c> when the game has no Seamless Co-op.</param>
public sealed record FromSoftSaveNames(string Vanilla, string? Modded, string? SeamlessCoop);
