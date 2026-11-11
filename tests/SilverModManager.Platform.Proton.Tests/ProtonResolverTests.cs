using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests;

public class ProtonResolverTests
{
    private const uint AppId = 1091500;

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _steam;
    private readonly Dictionary<string, string> _env = new();

    public ProtonResolverTests()
    {
        _steam = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        Write(_steam.Combine("steam.sh"), "");
    }

    private readonly ILogger<ProtonResolver> _logger = Substitute.For<ILogger<ProtonResolver>>();

    private ProtonResolver Resolver => new(new FakeEnvironment(_env), _logger);

    private int InformationLogs() => _logger.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)call.GetArguments()[0]! == LogLevel.Information);

    private void Write(AbsolutePath path, string contents)
    {
        _fs.CreateDirectory(path.Parent);
        _fs.WriteAllText(path, contents);
    }

    private AbsolutePath AddOfficial(string directoryName, AbsolutePath? library = null)
    {
        var dir = (library ?? _steam).Combine("steamapps/common").Combine(directoryName);
        Write(dir.Combine("proton"), "#!/usr/bin/env python3");
        Write(dir.Combine("toolmanifest.vdf"), "\"manifest\"\n{\n  \"version\" \"2\"\n}");
        return dir;
    }

    private AbsolutePath AddCustom(string directoryName, string? internalName, AbsolutePath? root = null)
    {
        var dir = (root ?? _steam.Combine("compatibilitytools.d")).Combine(directoryName);
        Write(dir.Combine("proton"), "#!/usr/bin/env python3");
        if (internalName is not null) Write(dir.Combine("compatibilitytool.vdf"), ToolVdf(internalName));
        return dir;
    }

    private static string ToolVdf(string internalName) => $$"""
        "compatibilitytools"
        {
          "compat_tools"
          {
            "{{internalName}}"
            {
              "install_path" "."
              "display_name" "Some display name"
              "from_oslist" "windows"
              "to_oslist" "linux"
            }
          }
        }
        """;

    private void WriteConfig(string mappings) => Write(_steam.Combine("config/config.vdf"), $$"""
        "InstallConfigStore"
        {
          "Software"
          {
            "Valve"
            {
              "Steam"
              {
                "CompatToolMapping"
                {
                  {{mappings}}
                }
              }
            }
          }
        }
        """);

    private static string Mapping(uint appId, string name) => $$"""
        "{{appId}}"
        {
          "name" "{{name}}"
          "config" ""
          "priority" "250"
        }
        """;

    [Fact]
    public void ListBuilds_SortsNaturally()
    {
        AddOfficial("Proton 10.0");
        AddOfficial("Proton 9.0");
        AddOfficial("Proton 8.0");
        AddOfficial("Proton - Experimental");
        AddOfficial("Proton Hotfix");

        Resolver.ListBuilds(_steam).Select(b => b.Directory.FileName)
            .Should().Equal("Proton - Experimental", "Proton 8.0", "Proton 9.0", "Proton 10.0", "Proton Hotfix");
    }

    [Fact]
    public void ListBuilds_OfficialBuildsUseSteamsMappingNames()
    {
        AddOfficial("Proton 9.0 (Beta)");
        AddOfficial("Proton - Experimental");
        AddOfficial("Proton Hotfix");
        AddOfficial("Proton 6.3");

        Resolver.ListBuilds(_steam).Select(b => b.Name)
            .Should().BeEquivalentTo("proton_9", "proton_experimental", "proton_hotfix", "proton_63");
    }

    [Fact]
    public void ListBuilds_LauncherIsTheProtonScript()
    {
        var dir = AddOfficial("Proton 9.0");

        Resolver.ListBuilds(_steam).Should().ContainSingle()
            .Which.Launcher.Should().Be(dir.Combine("proton"));
    }

    [Fact]
    public void ListBuilds_CustomToolNameComesFromTheVdf()
    {
        var dir = AddCustom("GE-Proton9-27", "GE-Proton9-27-internal");

        var build = Resolver.ListBuilds(_steam).Should().ContainSingle().Subject;
        build.Name.Should().Be("GE-Proton9-27-internal");
        build.Directory.Should().Be(dir);
    }

    [Fact]
    public void ListBuilds_DirectoryWithoutProtonScriptIsSkipped()
    {
        Write(_steam.Combine("compatibilitytools.d/NotProton/compatibilitytool.vdf"), ToolVdf("NotProton"));
        Write(_steam.Combine("steamapps/common/Proton Leftover/readme.txt"), "");

        Resolver.ListBuilds(_steam).Should().BeEmpty();
    }

    [Fact]
    public void ListBuilds_CustomToolWithoutCompatibilityToolVdf_IsSkippedAndLogged()
    {
        AddCustom("Half-Installed", internalName: null);

        Resolver.ListBuilds(_steam).Should().BeEmpty();
        InformationLogs().Should().BeGreaterThan(0);
    }

    [Fact]
    public void ListBuilds_OfficialBuildWithoutToolManifest_IsSkippedAndLogged()
    {
        var dir = AddOfficial("Proton 9.0");
        _fs.DeleteFile(dir.Combine("toolmanifest.vdf"));

        Resolver.ListBuilds(_steam).Should().BeEmpty();
        InformationLogs().Should().BeGreaterThan(0);
    }

    [Fact]
    public void ListBuilds_CompleteBuilds_AreKeptWithoutLogging()
    {
        AddOfficial("Proton 9.0");
        AddCustom("GE-Proton9-27", "GE-Proton9-27");

        Resolver.ListBuilds(_steam).Should().HaveCount(2);
        InformationLogs().Should().Be(0);
    }

    [Fact]
    public void ListBuilds_HonoursExtraCompatToolsPaths()
    {
        var extra1 = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("tools-a");
        var extra2 = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("tools-b");
        AddCustom("Proton-CachyOS", "proton-cachyos", extra1);
        AddCustom("Proton-EM", "proton-em", extra2);
        _env["STEAM_EXTRA_COMPAT_TOOLS_PATHS"] = $"{extra1}:{extra2}";

        Resolver.ListBuilds(_steam).Select(b => b.Name).Should().BeEquivalentTo("proton-cachyos", "proton-em");
    }

    [Fact]
    public void ListBuilds_IgnoresExtraPathsThatAreRelativeOrMissing()
    {
        _env["STEAM_EXTRA_COMPAT_TOOLS_PATHS"] = "relative/dir::/does/not/exist";

        Resolver.ListBuilds(_steam).Should().BeEmpty();
    }

    [Fact]
    public void ListBuilds_FindsProtonInSecondLibrary()
    {
        var second = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("games/SteamLibrary");
        AddOfficial("Proton 9.0", second);
        Write(_steam.Combine("steamapps/libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
              "0" { "path" "{{_steam}}" }
              "1" { "path" "{{second}}" }
            }
            """);

        Resolver.ListBuilds(_steam).Should().ContainSingle()
            .Which.Directory.Should().Be(second.Combine("steamapps/common/Proton 9.0"));
    }

    [Fact]
    public void ListBuilds_FirstBuildWithAGivenNameWins()
    {
        var user = AddCustom("Proton-Mine", "proton_9");
        AddOfficial("Proton 9.0");

        Resolver.ListBuilds(_steam).Should().ContainSingle()
            .Which.Directory.Should().Be(user);
    }

    [Fact]
    public void ResolveForApp_ReturnsTheMappedBuild()
    {
        AddOfficial("Proton 9.0");
        var ge = AddCustom("GE-Proton9-27", "GE-Proton9-27");
        WriteConfig(Mapping(AppId, "GE-Proton9-27"));

        var resolution = Resolver.ResolveForApp(_steam, AppId);

        resolution.IsResolved.Should().BeTrue();
        resolution.Build!.Directory.Should().Be(ge);
    }

    [Fact]
    public void ResolveForApp_MappedButNotInstalledIsADiagnosticNamingIt()
    {
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, "GE-Proton8-1"));

        var resolution = Resolver.ResolveForApp(_steam, AppId);

        resolution.IsResolved.Should().BeFalse();
        resolution.Build.Should().BeNull();
        resolution.Diagnostic.Should().Contain("GE-Proton8-1");
    }

    [Fact]
    public void ResolveForApp_UnmappedAppUsesTheDefaultMapping()
    {
        AddOfficial("Proton 9.0");
        var experimental = AddOfficial("Proton - Experimental");
        WriteConfig(Mapping(0, "proton_experimental") + "\n" + Mapping(42, "proton_9"));

        var resolution = Resolver.ResolveForApp(_steam, AppId);

        resolution.Build!.Directory.Should().Be(experimental);
    }

    [Fact]
    public void ResolveForApp_DefaultMappingThatIsNotInstalledIsADiagnostic()
    {
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(0, "proton_experimental"));

        var resolution = Resolver.ResolveForApp(_steam, AppId);

        resolution.IsResolved.Should().BeFalse();
        resolution.Diagnostic.Should().Contain("proton_experimental");
    }

    [Fact]
    public void ResolveForApp_NoMappingFallsBackToNewestStableValveBuild()
    {
        AddOfficial("Proton 8.0");
        var newest = AddOfficial("Proton 10.0");
        AddOfficial("Proton - Experimental");
        AddOfficial("Proton Hotfix");
        AddCustom("GE-Proton9-27", "GE-Proton9-27");
        WriteConfig(Mapping(42, "proton_8"));

        Resolver.ResolveForApp(_steam, AppId).Build!.Directory.Should().Be(newest);
    }

    [Fact]
    public void ResolveForApp_FallbackIgnoresCustomBuildsInADirectoryNamedCommon()
    {
        var valve = AddOfficial("Proton 9.0");
        var extra = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("tools/common");
        AddCustom("Proton-Custom 99", "custom-99", extra);
        _env["STEAM_EXTRA_COMPAT_TOOLS_PATHS"] = extra.ToString();

        Resolver.ResolveForApp(_steam, AppId).Build!.Directory.Should().Be(valve);
    }

    [Fact]
    public void ResolveForApp_ReadsConfigKeysCaseInsensitively()
    {
        AddOfficial("Proton 9.0");
        var ge = AddCustom("GE-Proton9-27", "GE-Proton9-27");
        Write(_steam.Combine("config/config.vdf"), $$"""
            "InstallConfigStore"
            {
              "software"
              {
                "valve"
                {
                  "steam"
                  {
                    "compattoolmapping"
                    {
                      {{Mapping(AppId, "GE-Proton9-27")}}
                    }
                  }
                }
              }
            }
            """);

        Resolver.ResolveForApp(_steam, AppId).Build!.Directory.Should().Be(ge);
    }

    [Fact]
    public void ResolveForApp_MissingConfigFallsBackToNewestStableBuild()
    {
        var newest = AddOfficial("Proton 9.0");
        AddOfficial("Proton 8.0");

        Resolver.ResolveForApp(_steam, AppId).Build!.Directory.Should().Be(newest);
    }

    [Fact]
    public void ResolveForApp_EmptyMappingNameCountsAsUnmapped()
    {
        var official = AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, ""));

        Resolver.ResolveForApp(_steam, AppId).Build!.Directory.Should().Be(official);
    }

    [Fact]
    public void ResolveForApp_NothingInstalledIsADiagnostic()
    {
        var resolution = Resolver.ResolveForApp(_steam, AppId);

        resolution.IsResolved.Should().BeFalse();
        resolution.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    private AbsolutePath CompatData(uint appId) => _steam.Combine($"steamapps/compatdata/{appId}");

    private void WriteConfigInfo(uint appId, AbsolutePath buildDir, string fontsSuffix = "/files/share/fonts/", string newline = "\n")
        => Write(CompatData(appId).Combine("config_info"), $"11.0-100{newline}{buildDir}{fontsSuffix}{newline}");

    [Fact]
    public void ResolveForPrefix_ThePrefixesOwnBuildWinsOverTheMapping()
    {
        var proton11 = AddOfficial("Proton 11.0");
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, "proton-stable"));
        WriteConfigInfo(AppId, proton11);

        var resolution = Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId));

        resolution.IsResolved.Should().BeTrue();
        resolution.Build!.Name.Should().Be("proton_11");
        resolution.Build.Directory.Should().Be(proton11);
    }

    [Fact]
    public void ResolveForPrefix_ThePrefixesOwnBuildMayBeACustomOne()
    {
        var ge = AddCustom("GE-Proton10-9", "GE-Proton10-9");
        WriteConfigInfo(AppId, ge);

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Name.Should().Be("GE-Proton10-9");
    }

    [Fact]
    public void ResolveForPrefix_AConfigInfoPathWithADotSegmentStillResolves()
    {
        var dir = AddOfficial("Proton 11.0");
        WriteConfigInfo(AppId, dir, fontsSuffix: "/./files/share/fonts/");

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Directory.Should().Be(dir);
    }

    [Fact]
    public void ResolveForPrefix_CrlfAndTrailingBlankLinesAreTolerated()
    {
        var dir = AddOfficial("Proton 11.0");
        WriteConfigInfo(AppId, dir, newline: "\r\n");
        _fs.WriteAllText(CompatData(AppId).Combine("config_info"), $"11.0-100\r\n{dir}/files/share/fonts/\r\n\r\n\r\n");

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Directory.Should().Be(dir);
    }

    [Fact]
    public void ResolveForPrefix_AnUninstalledPrefixBuildFallsBackToTheMapping()
    {
        WriteConfigInfo(AppId, _steam.Combine("steamapps/common/Proton 11.0"));
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, "proton_9"));

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Name.Should().Be("proton_9");
    }

    [Fact]
    public void ResolveForPrefix_ABuildWithNoManifestIsSkippedToTheMapping()
    {
        var dir = _steam.Combine("steamapps/common/Proton 11.0");
        Write(dir.Combine("proton"), "");
        WriteConfigInfo(AppId, dir);
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, "proton_9"));

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Name.Should().Be("proton_9");
    }

    [Fact]
    public void ResolveForPrefix_NoConfigInfoFallsBackToTheMapping()
    {
        WriteConfig(Mapping(AppId, "proton_9"));
        AddOfficial("Proton 9.0");

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Name.Should().Be("proton_9");
    }

    [Fact]
    public void ResolveForPrefix_AConfigInfoWithoutTheFontsSuffixFallsBackToTheMapping()
    {
        var dir = AddOfficial("Proton 11.0");
        AddOfficial("Proton 9.0");
        WriteConfig(Mapping(AppId, "proton_9"));
        WriteConfigInfo(AppId, dir, fontsSuffix: "/files/lib/");

        Resolver.ResolveForPrefix(_steam, AppId, CompatData(AppId)).Build!.Name.Should().Be("proton_9");
    }

    [Fact]
    public void ResolveForApp_MappedButNotInstalledSaysHowToRecordTheBuild()
    {
        WriteConfig(Mapping(AppId, "proton-stable"));
        AddOfficial("Proton 9.0");

        Resolver.ResolveForApp(_steam, AppId).Diagnostic.Should()
            .EndWith("Launch the game once through Steam so its prefix records the build, then run the tool again.");
    }

    [Fact]
    public void APrefixRecordedThroughASymlinkedParentStillResolvesItsBuild()
    {
        // Steam on an ostree host records /var/home/... for the tree that /home/... also reaches.
        var root = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"smm-resolver-{Guid.NewGuid():N}");
        try
        {
            var real = root.Combine("real");
            var link = root.Combine("link");
            var steam = real.Combine("steam");
            var build = steam.Combine("steamapps/common/Proton 11.0");
            FileSystem.Shared.CreateDirectory(build);
            FileSystem.Shared.WriteAllText(build.Combine("proton"), "");
            FileSystem.Shared.WriteAllText(build.Combine("toolmanifest.vdf"), "");
            var compatData = steam.Combine("steamapps/compatdata/42");
            FileSystem.Shared.CreateDirectory(compatData);
            Directory.CreateSymbolicLink(link.ToString(), real.ToString());
            FileSystem.Shared.WriteAllText(compatData.Combine("config_info"), $"11.0-100\n{link}/steam/steamapps/common/Proton 11.0/files/share/fonts/\n");

            var resolution = new ProtonResolver(new FakeEnvironment(_env)).ResolveForPrefix(steam, 42, compatData);

            resolution.IsResolved.Should().BeTrue();
            resolution.Build!.Name.Should().Be("proton_11");
        }
        finally
        {
            if (Directory.Exists(root.ToString())) Directory.Delete(root.ToString(), recursive: true);
        }
    }

    private sealed class FakeEnvironment(IReadOnlyDictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }
}
