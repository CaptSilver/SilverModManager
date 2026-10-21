using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Something a locator found wrong while it looked for games, such as two games claiming one store id or one
/// game installed twice. The locator carried on and located what it could; this is what it left out and why.
/// </summary>
/// <param name="Locator">Name of the locator that reported it.</param>
/// <param name="Message">A sentence that names the games, ids and paths involved.</param>
[PublicAPI]
public sealed record GameLocatorProblem(string Locator, string Message);
