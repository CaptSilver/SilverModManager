using JetBrains.Annotations;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// A <see cref="IDiagnosticWriter"/> that writes every value with <c>ToString</c>. It is for hosts without the UI's
/// value formatters, such as the command line; the UI registers its own writer ahead of it.
/// </summary>
[PublicAPI]
public sealed class PlainDiagnosticWriter : IDiagnosticWriter
{
    /// <inheritdoc/>
    public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull =>
        state.StringBuilder.Append(value.ToString());

    /// <inheritdoc/>
    public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct =>
        state.StringBuilder.Append(value.ToString());

    /// <inheritdoc/>
    public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value) =>
        state.StringBuilder.Append(value);
}
