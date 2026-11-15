using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Install;

internal static class FileContents
{
    /// <summary>Whether both files exist and hold the same bytes, so a rerun leaves an unchanged tool file alone.</summary>
    public static async Task<bool> SameBytes(AbsolutePath first, AbsolutePath second, CancellationToken cancellationToken)
    {
        if (!first.FileExists || !second.FileExists) return false;
        if (first.FileInfo.Size != second.FileInfo.Size) return false;

        await using var a = first.Read();
        await using var b = second.Read();
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var read = await a.ReadAtLeastAsync(bufferA, bufferA.Length, throwOnEndOfStream: false, cancellationToken);
            var readB = await b.ReadAtLeastAsync(bufferB, bufferB.Length, throwOnEndOfStream: false, cancellationToken);
            if (read != readB || !bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, readB))) return false;
            if (read == 0) return true;
        }
    }
}
