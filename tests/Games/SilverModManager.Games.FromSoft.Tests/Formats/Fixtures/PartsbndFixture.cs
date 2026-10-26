using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>How a built bundle is wrapped.</summary>
public enum DcxKind
{
    /// <summary>Through <see cref="Dcx.Write"/> with the game's packed format.</summary>
    Dflt,

    /// <summary>A stored Kraken stream: <see cref="Dcx.Write"/> refuses KRAK, so the container is assembled by hand.</summary>
    Kraken,
}

/// <summary>A DCX-wrapped binder in the shape of an equipment part (<c>parts/wp_a_0556.partsbnd.dcx</c>).</summary>
public static class PartsbndFixture
{
    /// <summary>The unwrapped binder in the game's format; <paramref name="entryFlags"/> is one decoded flag byte per member.</summary>
    public static byte[] Binder(FromSoftFormatTraits traits, IReadOnlyList<(int Id, string Name, byte[] Data)> members, IReadOnlyList<byte>? entryFlags = null) =>
        traits.Binder switch
        {
            BinderFormat.Bnd4 => Bnd4Fixture.Build(members, entryFlags: entryFlags),
            BinderFormat.Bnd3 => Bnd3Fixture.Build(members, entryFlags: entryFlags),
            var other => throw new NotSupportedException($"No fixture builds {other} archives."),
        };

    public static byte[] Build(
        FromSoftFormatTraits traits, IReadOnlyList<(int Id, string Name, byte[] Data)> members,
        DcxKind dcx = DcxKind.Dflt, IReadOnlyList<byte>? entryFlags = null)
    {
        var raw = Binder(traits, members, entryFlags);
        return dcx switch
        {
            DcxKind.Dflt => Dcx.Write(raw, traits.PackedWrite),
            DcxKind.Kraken => DcxFixture.Container("KRAK", raw.Length, KrakenFixture.Stored(raw)),
            var other => throw new NotSupportedException($"No fixture wraps a bundle as {other}."),
        };
    }
}
