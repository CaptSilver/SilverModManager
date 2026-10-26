using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>Writes a <c>.bhd</c>/<c>.bdt</c> pair the way the game ships them, for tests that read through <see cref="GameArchives"/>.</summary>
public static class GameArchiveFixture
{
    private static readonly byte[] AesKey = Enumerable.Range(0, 16).Select(i => (byte)(0xa0 + i)).ToArray();

    /// <summary>
    /// Writes <c>name.bhd</c> and <c>name.bdt</c> into <paramref name="folder"/> holding each file back to back after
    /// <paramref name="lead"/> bytes of unrelated data. <paramref name="stored"/> is the bytes the archive keeps,
    /// usually a DCX; the unpadded size is its length.
    /// </summary>
    public static void Write(string folder, string name, Bhd5Fixture.SigningKey key, FromSoftFormatTraits traits, bool encrypted, int lead, params (string Path, byte[] Stored)[] files)
        => WriteSized(folder, name, key, traits, encrypted, lead, files.Select(f => (f.Path, f.Stored, f.Stored.Length)).ToArray());

    /// <summary>As <see cref="Write"/>, with the unpadded size the index records for each file given, so a test can make it disagree with the bytes.</summary>
    public static void WriteSized(string folder, string name, Bhd5Fixture.SigningKey key, FromSoftFormatTraits traits, bool encrypted, int lead, IReadOnlyList<(string Path, byte[] Stored, int Unpadded)> files)
    {
        var entries = new List<Bhd5Fixture.Entry>();
        var payload = new MemoryStream();
        foreach (var (path, stored, unpadded) in files)
        {
            var padded = (stored.Length + 15) & ~15;
            entries.Add(new Bhd5Fixture.Entry(
                Bhd5.PathHash(Bhd5.Normalise(path), traits), padded, unpadded, lead + payload.Length,
                encrypted ? AesKey : null,
                encrypted ? [(0, padded), (-1, -1)] : null));
            payload.Write(encrypted ? Bhd5Fixture.EncryptPayload(stored, AesKey) : stored.Concat(new byte[padded - stored.Length]).ToArray());
        }

        File.WriteAllBytes(Path.Combine(folder, name + ".bhd"), Bhd5Fixture.Sign(Bhd5Fixture.Build(entries), key));
        var bdt = new byte[lead + payload.Length + 64];
        new Random(5).NextBytes(bdt);
        payload.ToArray().CopyTo(bdt, lead);
        File.WriteAllBytes(Path.Combine(folder, name + ".bdt"), bdt);
    }
}
