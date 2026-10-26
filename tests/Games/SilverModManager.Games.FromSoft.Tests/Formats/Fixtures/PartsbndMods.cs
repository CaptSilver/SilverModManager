using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>
/// The equipment part two mods edit differently: the vanilla bundle holds a texture archive, two models and two
/// animation sets. Glow changes the textures and the main model; Moveset changes the main model and both animation sets.
/// </summary>
public static class PartsbndMods
{
    public const string Glow = "Weapon Glow";
    public const string Moveset = "moveset_modpack";

    public const string Dir = @"N:\GR\data\INTERROOT_win64\parts\wp_a_0556\";
    public const string TpfName = Dir + "WP_A_0556.tpf";
    public const string FlverName = Dir + "WP_A_0556.flver";
    public const string Flver1Name = Dir + "WP_A_0556_1.flver";
    public const string AnibndName = Dir + "WP_A_0556.anibnd";
    public const string Anibnd1Name = Dir + "WP_A_0556_1.anibnd";

    public static byte[] Bytes(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] TpfOf(byte[] t1) => TpfFixture.Build([("T1", t1, 0), ("T2", Bytes(12, 64), 0)]);

    private static byte[] Anibnd(int seed, params int[] innerIds) =>
        Bnd4Fixture.Build([.. innerIds.Select(id => (id, $"a{id:D3}.hkx", Bytes(seed + id, 48)))]);

    public static IReadOnlyList<(int Id, string Name, byte[] Data)> Vanilla() =>
    [
        (100, TpfName, TpfOf(Bytes(11, 64))),
        (200, FlverName, Bytes(20, 300)),
        (201, Flver1Name, Bytes(21, 200)),
        (400, AnibndName, Anibnd(400, 1, 2, 3)),
        (401, Anibnd1Name, Anibnd(401, 1, 2, 3)),
    ];

    private static IReadOnlyList<(int Id, string Name, byte[] Data)> With(params (int Id, byte[] Data)[] changes) =>
        [.. Vanilla().Select(m => changes.Any(c => c.Id == m.Id) ? (m.Id, m.Name, changes.First(c => c.Id == m.Id).Data) : m)];

    public static IReadOnlyList<(int Id, string Name, byte[] Data)> GlowMembers() =>
        With((100, TpfOf(Bytes(111, 64))), (200, Bytes(120, 310)));

    public static IReadOnlyList<(int Id, string Name, byte[] Data)> MovesetMembers() =>
        With((200, Bytes(220, 290)), (400, Anibnd(1400, 7, 8)), (401, Anibnd(1401, 7, 8, 9)));

    public static byte[] Bundle(FromSoftFormatTraits traits, IReadOnlyList<(int Id, string Name, byte[] Data)> members) =>
        PartsbndFixture.Build(traits, members);

    public static Dictionary<int, byte[]> Read(byte[] bundle, FromSoftFormatTraits traits) =>
        Binders.For(traits).Read(Dcx.Read(bundle, traits)).ToDictionary(e => e.Id, e => e.Data);
}
