using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using NSubstitute;
using SteamKit2;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class LocalConfigVdfWriterTests : IDisposable
{
    private const string Shim = "/home/user/.local/share/silvermod/bin/silvermod";
    private const uint AppId = 1091500;

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-localconfig-").FullName;
    // Apart from the config's directory, which one test makes read-only.
    private readonly string _data = Directory.CreateTempSubdirectory("silvermod-data-").FullName;
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();

    public void Dispose()
    {
        // The read-only tests chmod directories; put them back so the tree can be deleted.
        foreach (var directory in Directory.EnumerateDirectories(_data, "*", SearchOption.AllDirectories).Append(_data).Append(_root))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_data, recursive: true);
    }

    private const string FluorineLine =
        "'/home/user/.local/share/fluorine/bin/fluorine-manager' -i 'Cyberpunk 2077' -p 'Default' run --headless -e 'Cyberpunk 2077' # %command%";

    private const string OtherManagerLine = "'/opt/tools/other-launcher' run # %command%";

    private string RecordFile => System.IO.Path.Combine(_data, "steam-launch-options", $"{AppId}.replaced");

    private AbsolutePath Record => FileSystem.Shared.FromUnsanitizedFullPath(RecordFile);

    private LocalConfigVdfWriter.Result Replace(string expectedOldLine, string loadout = "Survival", AbsolutePath? record = null)
        => Writer.ReplaceLaunchOptions(ConfigPath, AppId, Shim, loadout, [], [], expectedOldLine, record ?? Record);

    private LocalConfigVdfWriter Writer => new(_tracker);

    private string ConfigFile => System.IO.Path.Combine(_root, "localconfig.vdf");

    private AbsolutePath ConfigPath => FileSystem.Shared.FromUnsanitizedFullPath(ConfigFile);

    private string Backup => ConfigFile + ".silvermod-bak";

    private void WriteConfig(string? launchOptions, bool includeApp = true)
    {
        var appBlock = includeApp
            ? $"\t\t\t\t\"{AppId}\"\n\t\t\t\t{{\n\t\t\t\t\t\"LastPlayed\"\t\t\"1700000000\"\n"
              + (launchOptions is null ? "" : $"\t\t\t\t\t\"LaunchOptions\"\t\t\"{launchOptions}\"\n")
              + "\t\t\t\t}\n"
            : "";
        File.WriteAllText(ConfigFile,
            "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n"
            + "\t\t\t\t\"apps\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"730\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-novid\"\n\t\t\t\t\t}\n"
            + appBlock
            + "\t\t\t\t}\n\t\t\t\t\"AutoLaunchGameListing\"\t\t\"1\"\n\t\t\t}\n\t\t}\n\t}\n\t\"friends\"\n\t{\n\t\t\"PersonaName\"\t\t\"Tester\"\n\t}\n}\n");
    }

    private (string? Line, string? Other, string? Persona, string? Played) ReadBack()
    {
        var root = KeyValue.LoadFromString(File.ReadAllText(ConfigFile))!;
        var apps = root["Software"]["Valve"]["Steam"]["apps"];
        return (apps[AppId.ToString()]["LaunchOptions"].Value, apps["730"]["LaunchOptions"].Value,
            root["friends"]["PersonaName"].Value, apps[AppId.ToString()]["LastPlayed"].Value);
    }

    [Fact]
    public void Test_SetsSpanWhenAppHasNoLaunchOptions()
    {
        WriteConfig(null);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        result.Line.Should().Be($"'{Shim}' run 'Survival' -- %command%");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_KeepsUserPrefixAndTail()
    {
        WriteConfig("MANGOHUD=1 %command% -windowed");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.Line.Should().Be($"MANGOHUD=1 '{Shim}' run 'Survival' -- %command% -windowed");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_WritesLauncherSkipAndLeavesASecondWriteUnchanged()
    {
        WriteConfig("%command% -windowed");

        var first = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], ["--launcher-skip"]);
        var second = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], ["--launcher-skip"]);

        first.IsRefused.Should().BeFalse(first.Refusal);
        first.Line.Should().Be($"'{Shim}' run 'Survival' -- %command% -windowed --launcher-skip");
        second.Line.Should().Be(first.Line);
        ReadBack().Line.Should().Be(first.Line);
    }

    [Fact]
    public void Test_CreatesAppSectionWhenMissing()
    {
        WriteConfig(null, includeApp: false);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_LeavesUnrelatedKeysIntact()
    {
        WriteConfig("%command%");

        Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        var back = ReadBack();
        back.Other.Should().Be("-novid");
        back.Persona.Should().Be("Tester");
        back.Played.Should().Be("1700000000");
        KeyValue.LoadFromString(File.ReadAllText(ConfigFile))!["Software"]["Valve"]["Steam"]["AutoLaunchGameListing"].Value.Should().Be("1");
    }

    [Fact]
    public void Test_WritesBackupOfOriginalFirst()
    {
        WriteConfig("-foo %command%");
        var original = File.ReadAllText(ConfigFile);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        File.ReadAllText(Backup).Should().Be(original);
        result.Backup.Should().NotBeNull();
        result.Backup!.Value.ToString().Should().Be(Backup);
    }

    [Fact]
    public void Test_RefusesWhileSteamRunningAndWritesNothing()
    {
        WriteConfig(null);
        var original = File.ReadAllText(ConfigFile);
        _tracker.IsSteamRunning().Returns(true);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().StartWith("Steam rewrites its config on exit");
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(Backup).Should().BeFalse();
    }

    [Fact]
    public void Test_RefusesPrefixThatIsNotAPlainCommand()
    {
        WriteConfig("cd /tmp; %command%");
        var original = File.ReadAllText(ConfigFile);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Be(SteamLaunchOptionLine.Rewrite("cd /tmp; %command%", Shim, "Survival", [], []).Refusal);
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(Backup).Should().BeFalse();
    }

    [Fact]
    public void Test_RefusesMissingFile()
    {
        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("localconfig.vdf");
        File.Exists(ConfigFile).Should().BeFalse();
    }

    [Fact]
    public void Test_RefusesUnparsableFile()
    {
        File.WriteAllText(ConfigFile, "not { vdf");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeTrue();
        File.ReadAllText(ConfigFile).Should().Be("not { vdf");
    }

    [Fact]
    public void Test_ReplacesExistingSpanForOtherLoadout()
    {
        WriteConfig($"'{Shim}' run 'Old' -- %command%");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "New", [], []);

        result.Line.Should().Be($"'{Shim}' run 'New' -- %command%");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_RoundTripsLoadoutNameWithQuoteThroughVdf()
    {
        WriteConfig(null);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Bob's \"best\" run", [], []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        ReadBack().Line.Should().Be(result.Line);
        SteamLaunchOptionLine.Parse(ReadBack().Line!).LoadoutName.Should().Be("Bob's \"best\" run");
    }

    [Fact]
    public void Test_RoundTripsTheOverridesQuotesThroughVdf()
    {
        WriteConfig(null);
        WineDllOverride[] required = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]), new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", required, []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        File.ReadAllText(ConfigFile).Should().Contain("WINEDLLOVERRIDES=\\\"winmm=n,b;version=n,b\\\"");
        ReadBack().Line.Should().Be($"WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\" '{Shim}' run 'Survival' -- %command%");
    }

    [Fact]
    public void Test_RestoresTheConfigWhenTheOverridesDoNotReadBackThroughTheHealthChecksReader()
    {
        // Left byte for byte by the rewrite, but the reader strips only double quotes, so the entries come
        // back with a quote glued to the first and last DLL names.
        WriteConfig("WINEDLLOVERRIDES='winmm=n,b;version=n,b' %command%");
        var before = File.ReadAllText(ConfigFile);
        WineDllOverride[] required = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]), new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", required, []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("did not read back");
        File.ReadAllText(ConfigFile).Should().Be(before);
    }

    [Fact]
    public void Test_WriteRefusalOfAnotherManagersLineCarriesTheReplacement()
    {
        WriteConfig(FluorineLine);
        var original = File.ReadAllText(ConfigFile);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []);

        result.IsRefused.Should().BeTrue();
        result.Replacement.Should().NotBeNull();
        result.Replacement!.ManagerName.Should().Be("Fluorine");
        result.Replacement.OldLine.Should().Be(FluorineLine);
        result.Replacement.NewLine.Should().Be($"'{Shim}' run 'Survival' -- %command%");
        File.ReadAllText(ConfigFile).Should().Be(original);
    }

    [Fact]
    public void Test_WriteRefusalOfAnUnrelatedLineOffersNoReplacement()
    {
        WriteConfig("cd /tmp; %command%");

        Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", [], []).Replacement.Should().BeNull();
    }

    [Fact]
    public void Test_ReplaceSwapsTheLineWhenTheOldLineMatches()
    {
        WriteConfig(FluorineLine);
        var original = File.ReadAllText(ConfigFile);

        var result = Replace(FluorineLine);

        result.IsRefused.Should().BeFalse(result.Refusal);
        result.Line.Should().Be($"'{Shim}' run 'Survival' -- %command%");
        result.Replacement!.OldLine.Should().Be(FluorineLine);
        result.ReplacedLineRecord.Should().Be(Record);
        var back = ReadBack();
        back.Line.Should().Be(result.Line);
        back.Other.Should().Be("-novid");
        back.Persona.Should().Be("Tester");
        back.Played.Should().Be("1700000000");
        File.ReadAllText(Backup).Should().Be(original);
        result.Backup!.Value.ToString().Should().Be(Backup);
    }

    [Fact]
    public void Test_ReplaceRefusesWhenTheLineChangedSinceItWasShown()
    {
        WriteConfig(FluorineLine);
        var original = File.ReadAllText(ConfigFile);

        var result = Replace(FluorineLine.Replace("Default", "Defaulu"));

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().StartWith("The launch options are no longer the line that was shown.").And.Contain(FluorineLine);
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(Backup).Should().BeFalse();
        File.Exists(RecordFile).Should().BeFalse();
    }

    [Fact]
    public void Test_ReplaceRefusesAPlainUserLine()
    {
        WriteConfig("MANGOHUD=1 %command%");
        var original = File.ReadAllText(ConfigFile);

        var result = Replace("MANGOHUD=1 %command%");

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("not another manager's line");
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(RecordFile).Should().BeFalse();
    }

    [Fact]
    public void Test_ReplaceAppendsOneBlockPerReplacementToTheRecord()
    {
        WriteConfig(FluorineLine);
        Replace(FluorineLine).IsRefused.Should().BeFalse();
        WriteConfig(OtherManagerLine);
        Replace(OtherManagerLine, "Second").IsRefused.Should().BeFalse();

        var blocks = File.ReadAllText(RecordFile).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        blocks.Should().HaveCount(2);
        blocks[0].Split('\n').Should().SatisfyRespectively(
            l => l.Should().StartWith("replaced "),
            l => l.Should().Be($"config {ConfigFile}"),
            l => l.Should().Be($"app {AppId}"),
            l => l.Should().Be($"old {FluorineLine}"),
            l => l.Should().Be($"new '{Shim}' run 'Survival' -- %command%"));
        blocks[0].Split('\n')[0].Should().MatchRegex(@"^replaced \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z$");
        blocks[1].Should().Contain($"old {OtherManagerLine}").And.Contain("run 'Second'");
    }

    [Fact]
    public void Test_ReplaceWritesTheRecordBeforeTouchingSteamsFolder()
    {
        WriteConfig(FluorineLine);
        var before = File.ReadAllBytes(ConfigFile);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = Replace(FluorineLine);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("backup copy");
        File.ReadAllText(RecordFile).Should().Contain($"old {FluorineLine}");
        File.Exists(Backup).Should().BeFalse();
        File.ReadAllBytes(ConfigFile).Should().Equal(before);
    }

    [Fact]
    public void Test_ReplaceRefusesWhenTheRecordCannotBeWritten()
    {
        WriteConfig(FluorineLine);
        var before = File.ReadAllBytes(ConfigFile);
        var locked = Directory.CreateDirectory(System.IO.Path.Combine(_data, "locked"));
        File.SetUnixFileMode(locked.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var record = FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(locked.FullName, "steam-launch-options", "x.replaced"));

        var result = Replace(FluorineLine, record: record);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("record");
        File.ReadAllBytes(ConfigFile).Should().Equal(before);
        File.Exists(Backup).Should().BeFalse();
    }

    [Fact]
    public void Test_ReplaceRefusesWhileSteamRuns()
    {
        WriteConfig(FluorineLine);
        var original = File.ReadAllText(ConfigFile);
        _tracker.IsSteamRunning().Returns(true);

        var result = Replace(FluorineLine);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().StartWith("Steam rewrites its config on exit");
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(RecordFile).Should().BeFalse();
    }
}
