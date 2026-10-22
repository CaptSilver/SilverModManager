namespace NexusMods.Games.FromSoft.Formats;

/// <summary>One member of a binder archive.</summary>
/// <param name="Id">The numeric id the game looks the member up by; it is what a rebuild is keyed on.</param>
public sealed record BinderEntry(int Id, string Name, byte[] Data);

/// <summary>A binder archive format (BND3 or BND4): a flat list of named members.</summary>
public interface IBinder
{
    /// <summary>Parses the archive into its members, in file order.</summary>
    IReadOnlyList<BinderEntry> Read(ReadOnlySpan<byte> data);

    /// <summary>
    /// Returns <paramref name="source"/> with the data of the given members replaced. Which members exist never
    /// changes, so everything that is not member data is carried across from the source untouched.
    /// </summary>
    byte[] Rebuild(ReadOnlySpan<byte> source, IReadOnlyDictionary<int, byte[]> replacements);
}

/// <summary>Picks the binder reader for a game.</summary>
public static class Binders
{
    /// <summary>The binder that reads and rebuilds the archives of the game with these traits.</summary>
    /// <exception cref="NotSupportedException">The game's binder format is not one this build reads.</exception>
    public static IBinder For(FromSoftFormatTraits traits) => traits.Binder switch
    {
        BinderFormat.Bnd4 => Bnd4.Instance,
        var other => throw new NotSupportedException($"The {other} binder format is not read by this build."),
    };
}
