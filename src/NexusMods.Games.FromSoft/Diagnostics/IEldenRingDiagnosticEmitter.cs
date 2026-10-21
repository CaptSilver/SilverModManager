using NexusMods.Abstractions.Diagnostics.Emitters;

namespace NexusMods.Games.FromSoft.Diagnostics;

/// <summary>
/// Marks a diagnostic emitter as one of Elden Ring's. Emitters registered in DI under this interface are added to
/// the game's own list, so a new check is a registration and does not touch the game class.
/// </summary>
public interface IEldenRingDiagnosticEmitter : ILoadoutDiagnosticEmitter;
