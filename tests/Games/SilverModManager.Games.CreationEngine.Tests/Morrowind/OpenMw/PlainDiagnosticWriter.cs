using NexusMods.Abstractions.Diagnostics;

namespace NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;

internal sealed class PlainDiagnosticWriter : IDiagnosticWriter
{
    public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull => Write(ref state, value.ToString().AsSpan());

    public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct => Write(ref state, value.ToString().AsSpan());

    public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value) => state.StringBuilder.Append(value);
}
