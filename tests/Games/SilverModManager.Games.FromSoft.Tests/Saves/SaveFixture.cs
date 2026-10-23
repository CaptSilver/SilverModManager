using SilverModManager.Games.FromSoft.EldenRing;

namespace SilverModManager.Games.FromSoft.Tests.Saves;

/// <summary>A temp folder holding a save folder, a data folder for copies, and a Steam folder, all synthetic.</summary>
public sealed class SaveFixture : IDisposable
{
    public const string Account = "76561198000000001";

    public string Root { get; } = Directory.CreateTempSubdirectory("silvermod-saves-").FullName;

    public string SaveFolder => Path.Combine(Root, "prefix", Account);
    public string DataFolder => Path.Combine(Root, "data");
    public string SteamFolder => Path.Combine(Root, "steam");

    public SaveFixture() => Directory.CreateDirectory(SaveFolder);

    /// <summary>Writes a synthetic save: a BND4 container whose bodies hold the text, so two saves differ by content.</summary>
    public string Save(string name, string content)
    {
        var path = Path.Combine(SaveFolder, name);
        File.WriteAllBytes(path, Bnd4.Build([System.Text.Encoding.UTF8.GetBytes(content)]));
        return path;
    }

    public static byte[] BytesOf(string content) => Bnd4.Build([System.Text.Encoding.UTF8.GetBytes(content)]);

    /// <summary>The names of every file under the folder, relative to it, sorted.</summary>
    public static string[] Tree(string folder)
        => !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A snapshot of every file's name and bytes under the folder, to compare before and after an operation.</summary>
    public static Dictionary<string, byte[]> Snapshot(string folder)
        => Tree(folder).ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(folder, f)));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>A clock that only moves when told to, in the local time zone the stamps are written in.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => _now += by;
}
