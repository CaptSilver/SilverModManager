using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// Represents the state used in the <see cref="IDiagnosticWriter"/>.
/// </summary>
[PublicAPI]
public readonly struct DiagnosticWriterState
{
    /// <summary>
    /// <see cref="StringBuilder"/> used to create the output string.
    /// </summary>
    public readonly StringBuilder StringBuilder;

    /// <summary>
    /// How many backticks the template's own text has written so far. A writer that escapes outside text reads its
    /// parity to know whether a placeholder sits inside a code span the template opened. Backticks in finished Markdown
    /// or in escaped values are not counted: a code span with a backtick in it has an odd number of them.
    /// </summary>
    public readonly StrongBox<int> TemplateBackticks = new();

    /// <summary>
    /// Gets the mode of the writer.
    /// </summary>
    public readonly DiagnosticWriterMode Mode;

    /// <summary>
    /// Constructor.
    /// </summary>
    public DiagnosticWriterState(DiagnosticWriterMode mode, int capacity = 16)
    {
        StringBuilder = new StringBuilder(capacity);
        Mode = mode;
    }

    /// <summary>
    /// Produces a string output of the state.
    /// </summary>
    public string ToOutput() => StringBuilder.ToString();
}
