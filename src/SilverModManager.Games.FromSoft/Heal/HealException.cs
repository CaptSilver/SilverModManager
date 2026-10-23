namespace NexusMods.Games.FromSoft.Heal;

/// <summary>A heal could not be planned or completed; the message says what to do about it.</summary>
public class HealException(string message) : Exception(message);
