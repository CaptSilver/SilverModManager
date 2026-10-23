using SilverModManager.Abstractions.Diagnostics.Emitters;

namespace SilverModManager.Games.FromSoft.Diagnostics;

/// <summary>
/// Marks a diagnostic emitter as one of the FromSoft games'. Emitters registered in DI under this interface are
/// added to each game's own list, so a new check is a registration and does not touch the game class.
/// </summary>
public interface IFromSoftDiagnosticEmitter : ILoadoutDiagnosticEmitter;
