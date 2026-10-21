using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;
using SteamKit2;

namespace NexusMods.Platform.Proton.Tests.Steam;

public class LocalConfigVdfWriterTests : IDisposable
{
    private const string Shim = "/home/user/.local/share/silvermod/bin/silvermod";
    private const uint AppId = 1091500;

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-localconfig-").FullName;
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        result.Line.Should().Be($"'{Shim}' run 'Survival' -- %command%");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_KeepsUserPrefixAndTail()
    {
        WriteConfig("MANGOHUD=1 %command% -windowed");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.Line.Should().Be($"MANGOHUD=1 '{Shim}' run 'Survival' -- %command% -windowed");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_CreatesAppSectionWhenMissing()
    {
        WriteConfig(null, includeApp: false);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_LeavesUnrelatedKeysIntact()
    {
        WriteConfig("%command%");

        Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

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

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

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

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

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

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Be(SteamLaunchOptionLine.Rewrite("cd /tmp; %command%", Shim, "Survival", []).Refusal);
        File.ReadAllText(ConfigFile).Should().Be(original);
        File.Exists(Backup).Should().BeFalse();
    }

    [Fact]
    public void Test_RefusesMissingFile()
    {
        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("localconfig.vdf");
        File.Exists(ConfigFile).Should().BeFalse();
    }

    [Fact]
    public void Test_RefusesUnparsableFile()
    {
        File.WriteAllText(ConfigFile, "not { vdf");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", []);

        result.IsRefused.Should().BeTrue();
        File.ReadAllText(ConfigFile).Should().Be("not { vdf");
    }

    [Fact]
    public void Test_ReplacesExistingSpanForOtherLoadout()
    {
        WriteConfig($"'{Shim}' run 'Old' -- %command%");

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "New", []);

        result.Line.Should().Be($"'{Shim}' run 'New' -- %command%");
        ReadBack().Line.Should().Be(result.Line);
    }

    [Fact]
    public void Test_RoundTripsLoadoutNameWithQuoteThroughVdf()
    {
        WriteConfig(null);

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Bob's \"best\" run", []);

        result.IsRefused.Should().BeFalse(result.Refusal);
        ReadBack().Line.Should().Be(result.Line);
        SteamLaunchOptionLine.Parse(ReadBack().Line!).LoadoutName.Should().Be("Bob's \"best\" run");
    }

    [Fact]
    public void Test_RoundTripsTheOverridesQuotesThroughVdf()
    {
        WriteConfig(null);
        WineDllOverride[] required = [new("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]), new("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])];

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", required);

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

        var result = Writer.SetLauncherSpan(ConfigPath, AppId, Shim, "Survival", required);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("did not read back");
        File.ReadAllText(ConfigFile).Should().Be(before);
    }
}
