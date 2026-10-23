namespace SilverModManager.Games.FromSoft.Formats;

/// <summary>An AES call was given a key, IV or buffer of a size the cipher cannot take.</summary>
public class AesException(string message) : Exception(message);
