using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// Builds message archives (.msgbnd.dcx): a binder whose members are FMG tables, wrapped the way the game writes
/// them back. The binder layout and the wrapper both come from the game's traits.
/// </summary>
public static class MsgbndFixture
{
    /// <summary>The archive as the game ships it: the binder inside its DCX wrapper.</summary>
    public static byte[] Build(IReadOnlyDictionary<int, IReadOnlyDictionary<int, string?>> tables, FromSoftFormatTraits traits) =>
        Dcx.Write(BuildUncompressed(tables, traits), traits.PackedWrite);

    /// <summary>The bare binder, members ordered by id.</summary>
    public static byte[] BuildUncompressed(IReadOnlyDictionary<int, IReadOnlyDictionary<int, string?>> tables, FromSoftFormatTraits traits)
    {
        var entries = tables.OrderBy(t => t.Key)
            .Select(t => (Id: t.Key, Name: $"{t.Key}.fmg", Data: Fmg.Write(t.Value)))
            .ToList();
        return traits.Binder switch
        {
            BinderFormat.Bnd4 => Bnd4Fixture.Build(entries),
            BinderFormat.Bnd3 => Bnd3Fixture.Build(entries),
            var other => throw new NotSupportedException($"No fixture builds {other} archives."),
        };
    }

    /// <summary>Decodes an archive and parses every member as an FMG, keyed by member id.</summary>
    public static Dictionary<int, Dictionary<int, string?>> ReadBack(byte[] archive, FromSoftFormatTraits traits) =>
        Binders.For(traits).Read(Dcx.Read(archive, traits))
            .ToDictionary(e => e.Id, e => Fmg.Read(e.Data));
}
