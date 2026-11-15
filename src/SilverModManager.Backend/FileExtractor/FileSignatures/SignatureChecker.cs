using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.Sdk.FileExtractor;

namespace SilverModManager.Backend.FileExtractor.FileSignatures;

/// <summary>
/// Utility for checking the file types of specific files by matching the magic signatures on their headers.
/// </summary>
public class SignatureChecker : ISignatureChecker
{
    private readonly (FileType, byte[])[] _signatures;
    private readonly int _bufferSize;

    /// <summary>
    /// Creates a signature checker responsible for identifying file headers.
    /// </summary>
    /// <param name="inputs">The file types to check.</param>
    public SignatureChecker(params FileType[] inputs)
    {
        var types = new HashSet<FileType>(inputs);

        _signatures = Definitions.Signatures
            .Where(row => types.Contains(row.Item1))
            .OrderByDescending(row => row.Item2.Length)
            .ToArray();

        _bufferSize = _signatures.Length == 0 ? 0 : _signatures[0].Item2.Length;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> MatchesAnyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek) throw new ArgumentException(message: "Stream doesn't support seeking!", nameof(stream));
        var buffer = GC.AllocateUninitializedArray<byte>(length: _bufferSize);

        var originalPos = stream.Position;
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: cancellationToken);
        if (count < 1) return false;

        stream.Position = originalPos;

        foreach (var tuple in _signatures)
        {
            var (_, signature) = tuple;
            if (buffer.AsSpan(start: 0, length: count).StartsWith(signature))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<FileType>> MatchesAsync(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException(message: "Stream doesn't support seeking!", nameof(stream));
        var buffer = GC.AllocateUninitializedArray<byte>(length: _bufferSize);

        var originalPos = stream.Position;
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: default(CancellationToken));
        if (count < 1) return [];

        stream.Position = originalPos;

        var result = new List<FileType>();
        foreach (var tuple in _signatures)
        {
            var (fileType, signature) = tuple;
            if (buffer.AsSpan(start: 0, length: count).StartsWith(signature))
            {
                result.Add(fileType);
            }
        }

        return result;
    }
}
