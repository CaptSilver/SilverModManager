using FluentAssertions;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Saves;

public class SteamCloudSavesTests : IDisposable
{
    private readonly SaveFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static string Cache(string steamId, params (string File, int Size)[] files)
        => "\"1245620\"\n{\n" + string.Join("", files.Select(f => $"\t\"EldenRing/{steamId}/{f.File}\"\n\t{{\n\t\t\"root\"\t\t\"2\"\n\t\t\"size\"\t\t\"{f.Size}\"\n\t\t\"localtime\"\t\t\"1\"\n\t}}\n")) + "}\n";

    private void WriteCache(string account, string content)
    {
        var folder = Path.Combine(_fixture.SteamFolder, "userdata", account, "1245620");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "remotecache.vdf"), content);
    }

    private IReadOnlyList<CloudSave> Discover() => SteamCloudSaves.Discover(FileSystem.Shared.FromUnsanitizedFullPath(_fixture.SteamFolder));

    [Fact]
    public void FindsTheSavesOfEveryAccount()
    {
        WriteCache("65369667", Cache("76561198025635395", ("ER0000.sl2", 29360160), ("ER0000.sl2.bak", 29360160)));
        WriteCache("12345678", Cache("76561197972611406", ("ER0000.sl2", 100)));

        var found = Discover();

        found.Should().BeEquivalentTo(new CloudSave[]
        {
            new("12345678", "76561197972611406", "EldenRing/76561197972611406/ER0000.sl2", 100),
            new("65369667", "76561198025635395", "EldenRing/76561198025635395/ER0000.sl2", 29360160),
            new("65369667", "76561198025635395", "EldenRing/76561198025635395/ER0000.sl2.bak", 29360160),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void AnAccountWithoutTheGameHasNothingAndDoesNotHideTheOthers()
    {
        Directory.CreateDirectory(Path.Combine(_fixture.SteamFolder, "userdata", "1111", "999999"));
        WriteCache("2222", Cache("76561198000000001", ("ER0000.sl2", 5)));

        Discover().Should().ContainSingle().Which.AccountId.Should().Be("2222");
    }

    [Fact]
    public void AnUnreadableCacheIsSkippedAndTheOtherAccountsAreStillFound()
    {
        WriteCache("1111", Cache("76561198000000001", ("ER0000.sl2", 5)));
        WriteCache("2222", Cache("76561198000000002", ("ER0000.sl2", 6)));
        var unreadable = Path.Combine(_fixture.SteamFolder, "userdata", "1111", "1245620", "remotecache.vdf");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);

        try
        {
            Discover().Select(c => c.AccountId).Should().Equal("2222");
        }
        finally
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ASteamFolderWithoutUserdataHasNothing() => Discover().Should().BeEmpty();

    [Fact]
    public void FilesOfOtherGamesInTheCacheAreNotReported()
        => WriteCacheAndExpectNone("\"1245620\"\n{\n\t\"Other/1/file.dat\"\n\t{\n\t\t\"size\"\t\t\"1\"\n\t}\n}\n");

    private void WriteCacheAndExpectNone(string content)
    {
        WriteCache("1111", content);
        Discover().Should().BeEmpty();
    }

    [Fact]
    public void ThePurgeInstructionNamesTheAccountAndTheRemoteFile()
    {
        var instruction = new CloudSave("65369667", "76561198025635395", "EldenRing/76561198025635395/ER0000.sl2", 1).PurgeInstruction;

        instruction.Should().Contain("65369667").And.Contain("EldenRing/76561198025635395/ER0000.sl2").And.Contain("Steam Cloud");
    }
}
