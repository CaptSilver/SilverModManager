using System.Security.Cryptography;
using System.Text;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Masterlists;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Corpus;

/// <summary>A bundled file, its condition-free form, and the SHA-256 of each.</summary>
internal sealed record StrippedFile(string BundledSha256, string Stripped, string StrippedSha256);

/// <summary>
/// The bundled masterlists and prelude run through <see cref="MasterlistStripper"/>. The export test and
/// the corpus test both build their text here, so the generator and the sort see the same bytes.
/// </summary>
internal static class StrippedMasterlists
{
    private static readonly BundledMasterlistStore Store = new();

    public static StrippedFile Masterlist(string repository) => Strip(Store.MasterlistText(repository));

    public static StrippedFile Prelude() => Strip(Store.PreludeText());

    public static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static StrippedFile Strip(string bundled)
    {
        var stripped = MasterlistStripper.Strip(bundled);
        return new StrippedFile(Sha256(bundled), stripped, Sha256(stripped));
    }
}
