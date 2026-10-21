namespace NexusMods.Games.FromSoft.Saves;

/// <summary>
/// A save operation was refused before it changed anything. The message is written for the person using the page.
/// </summary>
public sealed class SaveRefusedException(string message) : Exception(message);
