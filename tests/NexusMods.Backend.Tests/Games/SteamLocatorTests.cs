using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Backend.Tests.Games;

public class SteamLocatorTests
{
    private const uint AppId = 1091500;

    [Fact]
    public void Locate_ProtonGame_ExposesLinuxCompatabilityDataProvider()
    {
        var result = LocateSingle(withProtonPrefix: true).Result;

        result.Platform.Should().Be(OSPlatform.Windows, "Proton runs the Windows build of the game");
        result.LinuxCompatabilityDataProvider.Should().NotBeNull();
        result.LinuxCompatabilityDataProvider!.WinePrefixDirectoryPath.FileName.Should().Be("pfx");
    }

    [Fact]
    public void Locate_WindowsBuild_RecordsSteamProtonWithThePrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: false);

        result.Compatibility.Should().Be(new CompatibilityLayer(CompatibilityLayerKind.SteamProton, libraryDirectory.Combine($"compatdata/{AppId}/pfx")));
    }

    [Fact]
    public void Locate_NativeLinuxBuild_RecordsNoCompatibilityLayer()
    {
        LocateSingle(withProtonPrefix: false, hasNativeLinuxBuild: true).Result.Compatibility.Should().BeNull();
    }

    [Fact]
    public void Locate_ProtonGame_ResolvesKnownFoldersInsideThePrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: true);
        var home = libraryDirectory.Combine($"compatdata/{AppId}/pfx/drive_c/users/steamuser");
        var fileSystem = result.Path.FileSystem;

        fileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Should().Be(home.Combine("AppData/Local"));
        fileSystem.GetKnownPath(KnownPath.ApplicationDataDirectory).Should().Be(home.Combine("AppData/Roaming"));
        fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory).Should().Be(home.Combine("Documents"));
        fileSystem.GetKnownPath(KnownPath.MyGamesDirectory).Should().Be(home.Combine("Documents/My Games"));
        result.WinePrefixPath.Should().Be(libraryDirectory.Combine($"compatdata/{AppId}/pfx"));
    }

    [Fact]
    public void Locate_ProtonGame_KeepsTheGameDirectory()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: true);

        result.Path.GetFullPath().Should().Be(libraryDirectory.Combine("common/Game").GetFullPath());
    }

    [Fact]
    public void Locate_EmptyCompatdataDirectory_IsNotAReadyPrefix()
    {
        var (result, _, libraryDirectory) = LocateSingle(withProtonPrefix: false, emptyCompatdata: true);

        result.Platform.Should().Be(OSPlatform.Windows);
        result.LinuxCompatabilityDataProvider.Should().BeNull("Proton has not written system.reg yet");
        result.WinePrefixPath.Should().Be(libraryDirectory.Combine($"compatdata/{AppId}/pfx"));
    }

    [Fact]
    public void Locate_WindowsGameWithoutProtonPrefix_ResolvesIntoThePrefixThatDoesNotExistYet()
    {
        var (result, hostFileSystem, libraryDirectory) = LocateSingle(withProtonPrefix: false);
        var expectedPrefix = libraryDirectory.Combine($"compatdata/{AppId}/pfx");

        result.Platform.Should().Be(OSPlatform.Windows);
        result.LinuxCompatabilityDataProvider.Should().BeNull();
        result.WinePrefixPath.Should().Be(expectedPrefix);

        KnownPath[] knownPaths =
        [
            KnownPath.LocalApplicationDataDirectory,
            KnownPath.ApplicationDataDirectory,
            KnownPath.MyDocumentsDirectory,
            KnownPath.MyGamesDirectory,
            KnownPath.HomeDirectory,
        ];

        foreach (var knownPath in knownPaths)
        {
            var resolved = result.Path.FileSystem.GetKnownPath(knownPath);
            resolved.GetFullPath().Should().StartWith(expectedPrefix.GetFullPath(), $"{knownPath} must not fall back to the host");
            resolved.Should().NotBe(hostFileSystem.GetKnownPath(knownPath));
        }
    }

    [Fact]
    public void Locate_ProtonGame_ExposesSteamInstallAndLocalConfigPaths()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var steamDirectory = hostFileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");

        var provider = result.LinuxCompatabilityDataProvider!;

        provider.SteamInstallPath.Should().Be(steamDirectory);
        // userdata is keyed by the 32-bit account id, which is the 64-bit LastOwner minus the individual-account base
        provider.SteamLocalConfigPath.Should().Be(steamDirectory.Combine("userdata/39734272/config/localconfig.vdf"));
    }

    [Fact]
    public async Task GetWineDllOverrides_ReadsTheFileAtSteamLocalConfigPath()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var provider = result.LinuxCompatabilityDataProvider!;
        WriteFile(hostFileSystem, provider.SteamLocalConfigPath, LocalConfig());

        var overrides = await provider.GetWineDllOverrides(CancellationToken.None);

        overrides.Should().ContainSingle().Which.DllName.Should().Be("winhttp");
    }

    [Fact]
    public async Task GetWineDllOverrides_ReadsTheGameEntryBesideANonSteamShortcut()
    {
        // Steam files a non-Steam shortcut under a negative signed id; a parser that insists every app id is
        // an unsigned integer rejects the whole file and the game's overrides are never seen.
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var provider = result.LinuxCompatabilityDataProvider!;
        WriteFile(hostFileSystem, provider.SteamLocalConfigPath, $$"""
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "-1045261181"
                                {
                                    "LaunchOptions" "-fullscreen"
                                }
                                "{{AppId}}"
                                {
                                    "LaunchOptions" "WINEDLLOVERRIDES=\"winmm=n,b;version=n,b\" '/opt/shim' run 'Loadout A' -- %command% --launcher-skip"
                                }
                            }
                        }
                    }
                }
            }
            """);

        var overrides = await provider.GetWineDllOverrides(CancellationToken.None);

        overrides.Select(o => o.DllName).Should().BeEquivalentTo("winmm", "version");
    }

    [Fact]
    public async Task GetWineDllOverrides_ToleratesADifferentKeyCase()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: true);
        var provider = result.LinuxCompatabilityDataProvider!;
        WriteFile(hostFileSystem, provider.SteamLocalConfigPath, LocalConfig().Replace("\"apps\"", "\"Apps\"").Replace("\"Software\"", "\"software\""));

        var overrides = await provider.GetWineDllOverrides(CancellationToken.None);

        overrides.Should().ContainSingle().Which.DllName.Should().Be("winhttp");
    }

    [Fact]
    public async Task GetWineDllOverrides_MissingFile_YieldsNothing()
    {
        var (result, _, _) = LocateSingle(withProtonPrefix: true);

        (await result.LinuxCompatabilityDataProvider!.GetWineDllOverrides(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void Locate_NativeLinuxGameWithoutProtonPrefix_UsesTheHostFileSystem()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: false, hasNativeLinuxBuild: true);

        result.Platform.Should().Be(OSPlatform.Linux);
        result.LinuxCompatabilityDataProvider.Should().BeNull();
        result.WinePrefixPath.Should().BeNull();
        result.Path.FileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory)
            .Should().Be(hostFileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory));
    }

    [Fact]
    public void Locate_NativeLinuxGameWithProtonPrefix_IsTheWindowsBuild()
    {
        var result = LocateSingle(withProtonPrefix: true, hasNativeLinuxBuild: true).Result;

        result.Platform.Should().Be(OSPlatform.Windows, "a ready prefix means the game ran through Proton");
        result.WinePrefixPath.Should().NotBeNull();
    }

    [Fact]
    public void Locate_RecordsTheSteamRootAsStoreRoot()
    {
        var (result, hostFileSystem, _) = LocateSingle(withProtonPrefix: false);

        result.StoreRoot.Should().Be(hostFileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam"));
    }

    [Fact]
    public void Locate_RecordsTheAppManifestBuildIdAsStoreBuildId()
    {
        var result = LocateSingle(withProtonPrefix: false, buildId: "9876543").Result;

        result.StoreBuildId.Should().Be("9876543");
    }

    [Theory]
    [InlineData("4", false)]
    [InlineData("6", true)]
    [InlineData("1026", true)]
    public void Locate_FlagsAnUpdateInProgress(string stateFlags, bool expected)
    {
        var result = LocateSingle(withProtonPrefix: false, stateFlags: stateFlags).Result;

        result.StoreUpdatePending.Should().Be(expected);
    }

    [Fact]
    public void Locate_UsesTheAppManifestsLastUpdatedAsStoreLastWrite()
    {
        var result = LocateSingle(withProtonPrefix: false, lastUpdated: "1700000000").Result;

        result.StoreLastWrite.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void Locate_LeavesStoreLastWriteUnknownWhenTheAppManifestHasNoUpdateTime()
    {
        // Steam rewrites the .acf on every launch, so its modification time says nothing about when the game files
        // were last written; with no update time the baseline reads every file instead.
        var result = LocateSingle(withProtonPrefix: false).Result;

        result.StoreLastWrite.Should().BeNull();
    }

    private const uint SecondAppId = 1091501;
    private const uint ThirdAppId = 1091502;

    [Fact]
    public void Locate_LibraryFolderThatDoesNotExist_LocatesTheGamesInTheOthers()
    {
        // An external drive that is unplugged leaves its path in libraryfolders.vdf for good.
        var fixture = ThreeLibraries();

        var results = fixture.Locate();

        results.Select(r => r.StoreIdentifier).Should().BeEquivalentTo(AppId.ToString(), SecondAppId.ToString());
    }

    [Theory]
    [InlineData(UnavailableStorage.EnumeratingTheLibrary)]
    [InlineData(UnavailableStorage.ReadingAnAppManifest)]
    [InlineData(UnavailableStorage.ReadingAnything)]
    public void Locate_LibraryFolderThatFailsWithAnIOException_LocatesTheGamesInTheOthers(UnavailableStorage failure)
    {
        // A USB drive behind an automount fails with ENODEV rather than "not found" while it is unplugged,
        // and the failure must stay with that library.
        var fixture = ThreeLibraries(unavailable: failure);

        var results = fixture.Locate();

        results.Select(r => r.StoreIdentifier).Should().BeEquivalentTo(AppId.ToString(), SecondAppId.ToString());
    }

    [Fact]
    public void Locate_OneAppManifestThatFailsWithAnIOException_LocatesTheOtherGamesInItsLibrary()
    {
        // A permissions mix-up can make one .acf unreadable while the rest of the library is fine.
        var logger = new CapturingLoggerFactory();
        var fixture = ThreeLibraries(unreadableManifestInMountedLibrary: true, loggerFactory: logger);

        var results = fixture.Locate();

        results.Select(r => r.StoreIdentifier).Should().BeEquivalentTo(AppId.ToString(), SecondAppId.ToString());
        logger.Warnings.Should().Contain(message => message.Contains($"appmanifest_{ThirdAppId}.acf"));
    }

    [Fact]
    public void Locate_LibraryFolderThatFailsWithAnIOException_LogsAWarningNamingThePath()
    {
        var logger = new CapturingLoggerFactory();
        var fixture = ThreeLibraries(unavailable: UnavailableStorage.ReadingAnything, loggerFactory: logger);

        fixture.Locate();

        logger.Warnings.Should().Contain(message => message.Contains(fixture.UnavailableLibrary.GetFullPath()));
    }

    /// <summary>
    /// What fails under the unavailable library. The drive being gone breaks all of them at once, but the
    /// narrower ones show which read the locator has to survive.
    /// </summary>
    public enum UnavailableStorage
    {
        None,
        EnumeratingTheLibrary,
        ReadingAnAppManifest,
        ReadingAnything,
    }

    private sealed record ThreeLibrariesFixture(SteamLocator Locator, AbsolutePath UnavailableLibrary)
    {
        public GameLocatorResult[] Locate() => Locator.Locate().ToArray();
    }

    /// <summary>
    /// Steam's own library, <c>/mnt/steam</c>, and an external drive at <c>/var/mnt/external</c> that either holds
    /// a game whose reads fail or, with <see cref="UnavailableStorage.None"/>, is not there at all. With
    /// <paramref name="unreadableManifestInMountedLibrary"/>, the third game sits in <c>/mnt/steam</c> instead and
    /// only its appmanifest fails to read.
    /// </summary>
    private static ThreeLibrariesFixture ThreeLibraries(
        UnavailableStorage unavailable = UnavailableStorage.None,
        bool unreadableManifestInMountedLibrary = false,
        ILoggerFactory? loggerFactory = null)
    {
        var fileSystem = new FailingFileSystem(OSInformation.FakeUnix);
        var steamDirectory = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        var mounted = fileSystem.FromUnsanitizedFullPath("/mnt/steam");
        var external = fileSystem.FromUnsanitizedFullPath("/var/mnt/external");

        WriteFile(fileSystem, steamDirectory.Combine("steam.sh"), "");
        WriteFile(fileSystem, steamDirectory.Combine("config/libraryfolders.vdf"), LibraryFolders([steamDirectory, mounted, external]));
        WriteFile(fileSystem, steamDirectory.Combine("steamapps/libraryfolders.vdf"), LibraryFolders([steamDirectory, mounted, external]));

        AddInstalledGame(fileSystem, steamDirectory.Combine("steamapps"), AppId);
        AddInstalledGame(fileSystem, mounted.Combine("steamapps"), SecondAppId);

        if (unreadableManifestInMountedLibrary)
        {
            AddInstalledGame(fileSystem, mounted.Combine("steamapps"), ThirdAppId);
            fileSystem.Unavailable = mounted.Combine($"steamapps/appmanifest_{ThirdAppId}.acf");
            fileSystem.FailingOperations = FailingFileSystem.Operations.Read;
        }

        if (unavailable != UnavailableStorage.None)
        {
            AddInstalledGame(fileSystem, external.Combine("steamapps"), ThirdAppId);
            fileSystem.Unavailable = external;
            fileSystem.FailingOperations = unavailable switch
            {
                UnavailableStorage.EnumeratingTheLibrary => FailingFileSystem.Operations.Enumerate,
                UnavailableStorage.ReadingAnAppManifest => FailingFileSystem.Operations.Read,
                _ => FailingFileSystem.Operations.All,
            };
        }

        var games = new[] { AppId, SecondAppId, ThirdAppId }.Select((appId, index) =>
        {
            var game = Substitute.For<IGameData>();
            game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From((uint)index + 1)) { SteamAppIds = [appId] });
            return game;
        }).ToArray();

        return new ThreeLibrariesFixture(new SteamLocator(games, loggerFactory ?? NullLoggerFactory.Instance, fileSystem, registry: null), external);
    }

    private static void AddInstalledGame(IFileSystem fileSystem, AbsolutePath libraryDirectory, uint appId)
    {
        WriteFile(fileSystem, libraryDirectory.Combine($"appmanifest_{appId}.acf"), AppManifest("1", "4", appId: appId));
        WriteFile(fileSystem, libraryDirectory.Combine($"common/Game{appId}/game.exe"), "");
    }

    /// <summary>
    /// An in-memory file system in which everything at or under <see cref="Unavailable"/> fails the way a missing
    /// automount does: with an <see cref="IOException"/> that is not a not-found error.
    /// </summary>
    private sealed class FailingFileSystem(IOSInformation os) : InMemoryFileSystem(os), IFileSystem
    {
        [Flags]
        public enum Operations { Enumerate = 1, Read = 2, All = Enumerate | Read }

        public AbsolutePath? Unavailable { get; set; }
        public Operations FailingOperations { get; set; }

        private void Check(AbsolutePath path, Operations operation)
        {
            if (Unavailable is not { } root || !FailingOperations.HasFlag(operation)) return;
            if (path.GetFullPath().StartsWith(root.GetFullPath(), StringComparison.Ordinal)) throw new IOException($"No such device: '{path}'");
        }

        bool IFileSystem.DirectoryExists(AbsolutePath path) { Check(path, Operations.Enumerate); return DirectoryExists(path); }
        bool IFileSystem.FileExists(AbsolutePath path) { Check(path, Operations.Read); return FileExists(path); }

        IEnumerable<AbsolutePath> IFileSystem.EnumerateFiles(AbsolutePath directory, string pattern, bool recursive) { Check(directory, Operations.Enumerate); return EnumerateFiles(directory, pattern, recursive); }
        IEnumerable<AbsolutePath> IFileSystem.EnumerateDirectories(AbsolutePath directory, string pattern, bool recursive) { Check(directory, Operations.Enumerate); return EnumerateDirectories(directory, pattern, recursive); }
        IEnumerable<IFileEntry> IFileSystem.EnumerateFileEntries(AbsolutePath directory, string pattern, bool recursive) { Check(directory, Operations.Enumerate); return EnumerateFileEntries(directory, pattern, recursive); }

        Stream IFileSystem.ReadFile(AbsolutePath path) { Check(path, Operations.Read); return ReadFile(path); }
        Stream IFileSystem.OpenFile(AbsolutePath path, FileMode mode, FileAccess access, FileShare share) { Check(path, Operations.Read); return OpenFile(path, mode, access, share); }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToArray(); } }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;
                lock (warnings) warnings.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
            }
        }
    }

    private static (GameLocatorResult Result, InMemoryFileSystem HostFileSystem, AbsolutePath LibraryDirectory) LocateSingle(
        bool withProtonPrefix,
        bool emptyCompatdata = false,
        bool hasNativeLinuxBuild = false,
        string buildId = "1",
        string stateFlags = "4",
        string? lastUpdated = null)
    {
        var fileSystem = new InMemoryFileSystem(OSInformation.FakeUnix);
        var steamDirectory = fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".steam/steam");
        var libraryDirectory = steamDirectory.Combine("steamapps");

        WriteFile(fileSystem, steamDirectory.Combine("steam.sh"), "");
        WriteFile(fileSystem, steamDirectory.Combine("config/libraryfolders.vdf"), LibraryFolders(steamDirectory));
        WriteFile(fileSystem, libraryDirectory.Combine("libraryfolders.vdf"), LibraryFolders(steamDirectory));
        WriteFile(fileSystem, libraryDirectory.Combine($"appmanifest_{AppId}.acf"), AppManifest(buildId, stateFlags, lastUpdated));
        WriteFile(fileSystem, libraryDirectory.Combine("common/Game/game.exe"), "");
        if (withProtonPrefix) WriteFile(fileSystem, libraryDirectory.Combine($"compatdata/{AppId}/pfx/system.reg"), "");
        if (emptyCompatdata) fileSystem.CreateDirectory(libraryDirectory.Combine($"compatdata/{AppId}"));

        var game = Substitute.For<IGameData>();
        game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From(1)) { SteamAppIds = [AppId] });
        game.HasNativeLinuxBuild.Returns(hasNativeLinuxBuild);

        var locator = new SteamLocator([game], NullLoggerFactory.Instance, fileSystem, registry: null);

        return (locator.Locate().Should().ContainSingle().Subject, fileSystem, libraryDirectory);
    }

    private static void WriteFile(IFileSystem fileSystem, AbsolutePath path, string contents)
    {
        fileSystem.CreateDirectory(path.Parent);
        fileSystem.WriteAllText(path, contents);
    }

    private static string LocalConfig() => $$"""
        "UserLocalConfigStore"
        {
            "Software"
            {
                "Valve"
                {
                    "Steam"
                    {
                        "apps"
                        {
                            "{{AppId}}"
                            {
                                "LaunchOptions" "WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
                            }
                        }
                    }
                }
            }
        }
        """;

    private static string LibraryFolders(AbsolutePath steamDirectory) => $$"""
        "libraryfolders"
        {
            "0"
            {
                "path" "{{steamDirectory}}"
                "label" ""
                "contentid" "1"
                "totalsize" "0"
                "apps"
                {
                    "{{AppId}}" "1"
                }
            }
        }
        """;

    private static string LibraryFolders(AbsolutePath[] libraries) => "\"libraryfolders\"\n{\n" + string.Join("\n", libraries.Select((library, index) => $$"""
        "{{index}}"
        {
            "path" "{{library}}"
            "label" ""
            "contentid" "{{index + 1}}"
            "totalsize" "0"
            "apps"
            {
            }
        }
        """)) + "\n}\n";

    private static string AppManifest(string buildId, string stateFlags, string? lastUpdated = null, uint appId = AppId) => $$"""
        "AppState"
        {
            "appid" "{{appId}}"
            "Universe" "1"
            "name" "Test Game"
            "StateFlags" "{{stateFlags}}"
            "installdir" "Game{{(appId == AppId ? "" : appId.ToString())}}"
            "LastOwner" "76561198000000000"
            "BuildID" "{{buildId}}"
            {{(lastUpdated is null ? "" : $"\"lastupdated\" \"{lastUpdated}\"")}}
            "InstalledDepots"
            {
                "1091501"
                {
                    "manifest" "123456789"
                    "size" "1"
                }
            }
        }
        """;
}
