namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>An archive index could not be decrypted or parsed, or a path is not in any of the game's archives.</summary>
public class Bhd5Exception(string message) : Exception(message);
