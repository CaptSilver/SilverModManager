namespace NexusMods.Networking.Steam.Exceptions;

/// <summary>
/// The Steam login could not be started or was rejected.
/// </summary>
public class SteamLoginException(string message) : Exception(message);
