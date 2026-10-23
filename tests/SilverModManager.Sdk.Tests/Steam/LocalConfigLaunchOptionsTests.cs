using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Steam;

namespace NexusMods.Sdk.Tests.Steam;

public class LocalConfigLaunchOptionsTests
{
    private const uint AppId = 1091500;
    private const string Line = "WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\" '/opt/shim' run 'Loadout A' -- %command% --launcher-skip";

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);

    [Fact]
    public void Read_FindsTheLineBesideANonSteamShortcutWithANegativeId()
    {
        var path = Write(Config("apps", $$"""
            "-1045261181" { "LaunchOptions" "-fullscreen" }
            "{{AppId}}" { "LaunchOptions" "WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\" '/opt/shim' run 'Loadout A' -- %command% --launcher-skip" }
            """));

        var result = LocalConfigLaunchOptions.Read(path, AppId);

        result.FileReadable.Should().BeTrue();
        result.Line.Should().Be(Line);
    }

    [Fact]
    public void Read_MatchesSectionNamesIgnoringCase()
    {
        var path = Write(Config("Apps", $$"""
            "{{AppId}}" { "launchoptions" "-windowed" }
            """));

        LocalConfigLaunchOptions.Read(path, AppId).Line.Should().Be("-windowed");
    }

    [Fact]
    public void Read_AppWithoutLaunchOptions_HasNoLine()
    {
        var path = Write(Config("apps", $$"""
            "{{AppId}}" { "Playtime" "5" }
            """));

        var result = LocalConfigLaunchOptions.Read(path, AppId);

        result.FileReadable.Should().BeTrue();
        result.Line.Should().BeNull();
    }

    [Fact]
    public void Read_EmptyLaunchOptions_HasNoLine()
    {
        var path = Write(Config("apps", $$"""
            "{{AppId}}" { "LaunchOptions" "" }
            """));

        LocalConfigLaunchOptions.Read(path, AppId).Line.Should().BeNull();
    }

    [Fact]
    public void Read_MissingFile_IsNotReadable()
    {
        var result = LocalConfigLaunchOptions.Read(_fs.FromUnsanitizedFullPath("/steam/missing.vdf"), AppId);

        result.FileReadable.Should().BeFalse();
        result.Line.Should().BeNull();
    }

    [Fact]
    public void Read_MalformedFile_IsNotReadable()
    {
        var path = Write("\"UserLocalConfigStore\" { \"Software\" {");

        var result = LocalConfigLaunchOptions.Read(path, AppId);

        result.FileReadable.Should().BeFalse();
        result.Line.Should().BeNull();
    }

    private AbsolutePath Write(string contents)
    {
        var path = _fs.FromUnsanitizedFullPath("/steam/userdata/1/config/localconfig.vdf");
        _fs.CreateDirectory(path.Parent);
        _fs.WriteAllText(path, contents);
        return path;
    }

    private static string Config(string appsKey, string apps) => $$"""
        "UserLocalConfigStore"
        {
            "Software" { "Valve" { "Steam" { "{{appsKey}}" { {{apps}} } } } }
        }
        """;
}
