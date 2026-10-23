namespace NexusMods.Games.CreationEngine.Creations;

/// <summary>
/// A Creations catalog or manifest exists but is not in the format the game writes. The recognizer turns it
/// into an unreadable record set so the caller keeps what it already knows.
/// </summary>
public sealed class CreationsRecordException : Exception
{
    public CreationsRecordException(string message, Exception? inner = null) : base(message, inner) { }
}
