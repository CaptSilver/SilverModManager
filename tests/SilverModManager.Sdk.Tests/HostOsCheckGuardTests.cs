using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// The app only runs on Linux, so code that asks "which OS am I on?" is dead weight. Games are
/// Windows binaries run under Proton/Wine though, so code that asks "which OS does the game
/// target?" has to stay. This test scans <c>src</c> for OS checks and fails on any line that
/// isn't in the allowlist below, which forces a new check to be justified as target-OS logic.
/// </summary>
public partial class HostOsCheckGuardTests
{
    // Entries are keyed by file and the exact trimmed source line, not by line number, so they
    // survive unrelated edits but a new check in an allowlisted file still fails.
    private static readonly (string File, string Line, string Reason)[] Allowed =
    [
        // GOG's installer list is data from their API (one entry per OS), not a host check.
        ("src/SilverModManager.Abstractions.GOG/IClient.cs", "ValueTask<(OSPlatform OS, Uri DownloadLink)[]> GetInstallers(ProductId productId, CancellationToken cancellationToken);", "GOG installer OS field"),
        ("src/SilverModManager.Abstractions.GOG/IClient.cs", "ValueTask DownloadInstallerArchive((OSPlatform OS, Uri DownloadLink) installerInfo, Stream output, CancellationToken cancellationToken);", "GOG installer OS field"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "public async ValueTask<(OSPlatform OS, Uri DownloadLink)[]> GetInstallers(ProductId productId, CancellationToken token)", "GOG installer OS field"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "return await _pipeline.ExecuteAsync<(OSPlatform OS, Uri DownloadLink)[]>(async cancellationToken =>", "GOG installer OS field"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "\"linux\" => OSPlatform.Linux,", "maps GOG's installer OS string"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "\"osx\" or \"mac\" => OSPlatform.OSX,", "maps GOG's installer OS string"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "\"windows\" => OSPlatform.Windows,", "maps GOG's installer OS string"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "_ => OSPlatform.Create(installer.OS),", "maps GOG's installer OS string"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "public async ValueTask DownloadInstallerArchive((OSPlatform OS, Uri DownloadLink) installerInfo, Stream output, CancellationToken token)", "GOG installer OS field"),
        ("src/SilverModManager.Networking.GOG/Client.cs", "if (installerInfo.OS != OSPlatform.Linux) throw new NotSupportedException();", "only the Linux installer is downloadable"),
        ("src/SilverModManager.Networking.GOG/CLI/Verbs.cs", "if (!installers.TryGetFirst(x => x.OS == OSPlatform.Linux, out var linuxInstaller)) return;", "picks GOG's Linux installer from their list"),

        // Located games: native Linux builds are Linux, builds run under Proton are the Windows build.
        ("src/SilverModManager.Backend/Games/Locators/SteamLocator.cs", "Platform = isWindowsBuild ? OSPlatform.Windows : OSPlatform.Linux,", "Proton runs the Windows build of the game, a native install without a prefix is Linux"),
        ("src/SilverModManager.Sdk/Games/Locators/GameLocatorResult.cs", "public OSPlatform Platform { get; init; } = OSPlatform.Linux;", "target OS of the located game, native Linux unless a locator says otherwise"),
        ("src/SilverModManager.Backend/Games/Locators/Heroic/HeroicGOGLocator.cs", "if (game.Platform != OSPlatform.Windows) return null;", "only Heroic's Windows builds run in a Wine or Proton prefix"),
        ("src/SilverModManager.Backend/Games/Locators/Heroic/HeroicGOGLocator.cs", "WinePrefixPath = gameFinderGame.Platform == OSPlatform.Windows ? WinePrefix(gameFinderGame, settings) : null,", "a native Linux build of the game has no prefix"),
        ("src/SilverModManager.Backend/Games/Locators/Heroic/HeroicEpicLocator.cs", "Platform = isWindows ? OSPlatform.Windows : OSPlatform.Linux,", "Legendary's platform field names the build it installed; only a Linux build runs without a prefix"),
        ("src/SilverModManager.Backend/Games/Locators/Heroic/HeroicAmazonLocator.cs", "Platform = OSPlatform.Windows,", "Amazon Games ships Windows builds only, run in a prefix"),
        ("src/SilverModManager.Backend/Games/Locators/Lutris/LutrisLocator.cs", "Platform = isWine ? OSPlatform.Windows : OSPlatform.Linux,", "a Lutris game on the Wine runner is the Windows build; any other runner runs it natively"),
        ("src/SilverModManager.Backend/Games/Locators/PrimaryFileProbe.cs", "Platform = OSPlatform.Windows,", "the installation used to ask a game for its executable name stands for a Windows build under Wine"),
        ("src/SilverModManager.Backend/Games/UserGames/AddGameByPathService.cs", "var platform = isNative ? OSPlatform.Linux : OSPlatform.Windows;", "a game added by path is recorded as its native build when its folder holds one, otherwise as the Windows build run in the given prefix"),
        ("src/SilverModManager.Games.Larian/BaldursGate3/BaldursGate3.cs", "internal static bool IsNativeBuild(GameLocatorResult result) => result.TargetOS.IsLinux && result.WinePrefixPath is null;", "the native Linux build keeps its data under XDG_DATA_HOME and starts bin/bg3; the Windows build runs in a prefix"),
        ("src/SilverModManager.Platform.Proton/PrefixResolver.cs", "if (result.Platform != OSPlatform.Windows)", "a native Linux build of the game has no prefix"),
        ("src/SilverModManager.Sdk/Games/UserGames/IUserGameInstallStore.cs", "public sealed record UserGameInstall(GameId Game, string Path, OSPlatform Platform, string? Prefix = null, string? Wine = null);", "which build of the game the user recorded in the folder"),
        ("src/SilverModManager.Backend/Games/UserGames/YamlUserGameInstallStore.cs", "private static readonly (string Name, OSPlatform Platform)[] Platforms = [(\"windows\", OSPlatform.Windows), (\"linux\", OSPlatform.Linux)];", "maps installs.yaml's names for the recorded game build"),
        ("src/SilverModManager.Abstractions.Games/RunGameTool.cs", "return locatorResult.TargetOS.IsWindows || locatorResult.Compatibility is not null;", "a launcher or loader of the Windows build runs in the game's prefix"),

        // Target OS of the located game: Proton runs the Windows build, native installs are Linux.
        ("src/SilverModManager.Games.StardewValley/StardewValley.cs", "return installation.LocatorResult.TargetOS.IsWindows", "game executable name differs between the Windows and Linux builds"),
        ("src/SilverModManager.Games.StardewValley/Installers/SMAPIInstaller.cs", "var targetParentName = targetOS.IsWindows ? WindowsFolder : LinuxFolder;", "SMAPI's install data has a folder per target OS"),
        ("src/SilverModManager.Games.StardewValley/Installers/SMAPIInstaller.cs", "var isUnix = targetOS.IsUnix();", "SMAPI ships different launchers for the Windows and Linux builds"),
        ("src/SilverModManager.Games.StardewValley/WebAPI/SMAPIWebApi.cs", "return os.IsWindows ? Platform.Windows : Platform.Linux;", "SMAPI web API platform, which should be the game's target OS; the Stardew emitters wrongly pass the host IOSInformation"),

        ("src/SilverModManager.Games.FileHashes/LocalBaselineCoordinator.cs", "=> locator.TargetOS.IsWindows ? OperatingSystem.Windows : locator.TargetOS.IsOSX ? OperatingSystem.MacOS : OperatingSystem.Linux;", "the baseline's version row records the OS of the game build that was installed"),

        // Real IO on the host filesystem.
        ("src/SilverModManager.Games.FileHashes/FileHashesService.cs", "sourceDirName: extractionDirectory.Path.ToNativeSeparators(OSInformation.Shared),", "native separators for Directory.Move"),
        ("src/SilverModManager.Games.FileHashes/FileHashesService.cs", "destDirName: finalDirectory.ToNativeSeparators(OSInformation.Shared)", "native separators for Directory.Move"),
        ("src/SilverModManager.Backend/ServiceExtensions.cs", "os ??= OSInformation.Shared;", "DI root for the injected IOSInformation"),

        // The file-lock probe only works with Windows file sharing semantics and returns false everywhere else.
    ];

    [GeneratedRegex(@"\.Is(Windows|Linux|OSX|MacOS|Unix)\b|OSPlatform\b|OSInformation\.Shared|FileSystem\.Shared\.OS|OperatingSystem\.Is\w+|MatchPlatform|\bon(Windows|Linux|OSX)\s*:")]
    private static partial Regex HostOsCheck();

    [Fact]
    public void Test_NoUnlistedOsChecksInSource()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs");
        var found = RepoScanGuard.FindLines(root, files, line => !line.StartsWith("//") && HostOsCheck().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, Allowed, "the app only runs on Linux; only checks on the game's target OS belong in src. Collapse the check or add it to the allowlist with a reason");
    }
}
