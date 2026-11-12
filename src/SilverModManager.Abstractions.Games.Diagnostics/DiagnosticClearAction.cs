using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// Something the user can clear from a loadout's diagnostics, bound to that loadout.
/// </summary>
/// <param name="Label">The button text.</param>
/// <param name="ClearAsync">Clears it and returns a sentence saying what was done.</param>
[PublicAPI]
public sealed record DiagnosticClearAction(string Label, Func<CancellationToken, Task<string>> ClearAsync);
