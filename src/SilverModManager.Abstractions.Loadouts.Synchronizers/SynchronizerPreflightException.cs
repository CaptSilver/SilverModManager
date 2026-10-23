using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Thrown before the synchronizer touches anything, because a write it was asked to do could not succeed:
/// the game's Proton prefix does not exist yet, or a folder it would write into is read-only.
/// </summary>
/// <remarks>
/// The message is written for the player and names what to do about it, so a UI can show it as it is.
/// </remarks>
[PublicAPI]
public class SynchronizerPreflightException(string message) : Exception(message);
