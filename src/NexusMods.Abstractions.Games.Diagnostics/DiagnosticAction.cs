using System.Collections.Immutable;
using JetBrains.Annotations;

namespace NexusMods.Abstractions.Diagnostics;

/// <summary>
/// A fix a diagnostic offers. The emitter only names it; the UI maps <see cref="Id"/> to the code that
/// runs, so emitters stay free of UI types and an id nobody handles simply shows no button.
/// </summary>
/// <param name="Id">Stable identifier of the action, see <see cref="DiagnosticActions"/>.</param>
/// <param name="Label">The text of the button.</param>
/// <param name="Arguments">Extra strings for the handler; most actions need none.</param>
[PublicAPI]
public sealed record DiagnosticAction(string Id, string Label, ImmutableArray<string> Arguments)
{
    /// <inheritdoc/>
    public bool Equals(DiagnosticAction? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id && Label == other.Label && Arguments.AsSpan().SequenceEqual(other.Arguments.AsSpan());
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Label);
        foreach (var argument in Arguments) hash.Add(argument);
        return hash.ToHashCode();
    }
}

/// <summary>
/// The actions emitters can attach to a <see cref="Diagnostic"/>.
/// </summary>
[PublicAPI]
public static class DiagnosticActions
{
    /// <summary>
    /// Id of the action that opens "Use this loadout from Steam" for the diagnostic's loadout.
    /// </summary>
    public const string UseLoadoutFromSteamId = "use-loadout-from-steam";

    /// <summary>
    /// Opens the dialog that writes the loadout's launch line into Steam's launch options.
    /// </summary>
    public static DiagnosticAction UseLoadoutFromSteam() => new(UseLoadoutFromSteamId, "Use this loadout from Steam", []);
}
