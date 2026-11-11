using System.Text;
using CliWrap;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>A PipeTarget that keeps the last <see cref="Capacity"/> bytes of a stream; <see cref="Text"/> is the kept bytes from the first complete line, as UTF-8.</summary>
public sealed class OutputTail : PipeTarget
{
    /// <summary>How many bytes of a stream are kept.</summary>
    public const int Capacity = 16 * 1024;

    private readonly byte[] _ring = new byte[Capacity];
    private readonly object _lock = new();
    private int _next;
    private bool _wrapped;

    /// <inheritdoc/>
    public override async Task CopyFromAsync(Stream origin, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        int read;
        while ((read = await origin.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            lock (_lock) Append(buffer.AsSpan(0, read));
        }
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        // Only the last Capacity bytes of an oversized chunk can survive.
        if (chunk.Length >= Capacity)
        {
            chunk = chunk[^Capacity..];
            _wrapped = true;
        }

        var firstPart = Math.Min(chunk.Length, Capacity - _next);
        chunk[..firstPart].CopyTo(_ring.AsSpan(_next));
        chunk[firstPart..].CopyTo(_ring);

        var end = _next + chunk.Length;
        if (end >= Capacity) _wrapped = true;
        _next = end % Capacity;
    }

    /// <summary>The kept bytes from the first complete line, as UTF-8.</summary>
    public string Text
    {
        get
        {
            byte[] linear;
            bool wrapped;
            lock (_lock)
            {
                wrapped = _wrapped;
                if (!wrapped) linear = _ring.AsSpan(0, _next).ToArray();
                else
                {
                    linear = new byte[Capacity];
                    _ring.AsSpan(_next).CopyTo(linear);
                    _ring.AsSpan(0, _next).CopyTo(linear.AsSpan(Capacity - _next));
                }
            }

            var start = 0;
            if (wrapped)
            {
                // The oldest kept line was probably cut, so it never leads; a tail with no newline is kept whole.
                var newline = Array.IndexOf(linear, (byte)'\n');
                if (newline >= 0) start = newline + 1;
            }
            return Encoding.UTF8.GetString(linear, start, linear.Length - start);
        }
    }
}
