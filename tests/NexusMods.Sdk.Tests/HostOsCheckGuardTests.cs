using System.Text.RegularExpressions;
using FluentAssertions;

namespace NexusMods.Sdk.Tests;

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
        ("src/NexusMods.Abstractions.GOG/IClient.cs", "ValueTask<(OSPlatform OS, Uri DownloadLink)[]> GetInstallers(ProductId productId, CancellationToken cancellationToken);", "GOG installer OS field"),
        ("src/NexusMods.Abstractions.GOG/IClient.cs", "ValueTask DownloadInstallerArchive((OSPlatform OS, Uri DownloadLink) installerInfo, Stream output, CancellationToken cancellationToken);", "GOG installer OS field"),
        ("src/NexusMods.Networking.GOG/Client.cs", "public async ValueTask<(OSPlatform OS, Uri DownloadLink)[]> GetInstallers(ProductId productId, CancellationToken token)", "GOG installer OS field"),
        ("src/NexusMods.Networking.GOG/Client.cs", "return await _pipeline.ExecuteAsync<(OSPlatform OS, Uri DownloadLink)[]>(async cancellationToken =>", "GOG installer OS field"),
        ("src/NexusMods.Networking.GOG/Client.cs", "\"linux\" => OSPlatform.Linux,", "maps GOG's installer OS string"),
        ("src/NexusMods.Networking.GOG/Client.cs", "\"osx\" or \"mac\" => OSPlatform.OSX,", "maps GOG's installer OS string"),
        ("src/NexusMods.Networking.GOG/Client.cs", "\"windows\" => OSPlatform.Windows,", "maps GOG's installer OS string"),
        ("src/NexusMods.Networking.GOG/Client.cs", "_ => OSPlatform.Create(installer.OS),", "maps GOG's installer OS string"),
        ("src/NexusMods.Networking.GOG/Client.cs", "public async ValueTask DownloadInstallerArchive((OSPlatform OS, Uri DownloadLink) installerInfo, Stream output, CancellationToken token)", "GOG installer OS field"),
        ("src/NexusMods.Networking.GOG/Client.cs", "if (installerInfo.OS != OSPlatform.Linux) throw new NotSupportedException();", "only the Linux installer is downloadable"),
        ("src/NexusMods.Networking.GOG/CLI/Verbs.cs", "if (!installers.TryGetFirst(x => x.OS == OSPlatform.Linux, out var linuxInstaller)) return;", "picks GOG's Linux installer from their list"),

        // Located games: native Linux builds are Linux, builds run under Proton are the Windows build.
        ("src/NexusMods.Backend/Games/Locators/SteamLocator.cs", "var platform = linuxCompatibilityDataProvider is null ? OSPlatform.Linux : OSPlatform.Windows;", "Proton runs the Windows build of the game"),
        ("src/NexusMods.Sdk/Games/Locators/GameLocatorResult.cs", "public OSPlatform Platform { get; init; } = OSPlatform.Linux;", "target OS of the located game, native Linux unless a locator says otherwise"),

        // Target OS of the located game: Proton runs the Windows build, native installs are Linux.
        ("src/NexusMods.Games.StardewValley/StardewValley.cs", "return installation.LocatorResult.TargetOS.IsWindows", "game executable name differs between the Windows and Linux builds"),
        ("src/NexusMods.Games.StardewValley/Installers/SMAPIInstaller.cs", "var targetParentName = targetOS.IsWindows ? WindowsFolder : LinuxFolder;", "SMAPI's install data has a folder per target OS"),
        ("src/NexusMods.Games.StardewValley/Installers/SMAPIInstaller.cs", "var isUnix = targetOS.IsUnix();", "SMAPI ships different launchers for the Windows and Linux builds"),
        ("src/NexusMods.Games.StardewValley/WebAPI/SMAPIWebApi.cs", "return os.IsWindows ? Platform.Windows : Platform.Linux;", "SMAPI web API platform, which should be the game's target OS; the Stardew emitters wrongly pass the host IOSInformation"),

        // Real IO on the host filesystem.
        ("src/NexusMods.Games.FileHashes/FileHashesService.cs", "sourceDirName: extractionDirectory.Path.ToNativeSeparators(OSInformation.Shared),", "native separators for Directory.Move"),
        ("src/NexusMods.Games.FileHashes/FileHashesService.cs", "destDirName: finalDirectory.ToNativeSeparators(OSInformation.Shared)", "native separators for Directory.Move"),
        ("src/NexusMods.Backend/ServiceExtensions.cs", "os ??= OSInformation.Shared;", "DI root for the injected IOSInformation"),

        // The file-lock probe only works with Windows file sharing semantics and returns false everywhere else.
        ("src/NexusMods.DataModel/Synchronizer/SynchronizerService.cs", "if (!FileSystem.Shared.OS.IsWindows)", "file-lock probe is Windows-only and a no-op on Linux"),
    ];

    [GeneratedRegex(@"\.Is(Windows|Linux|OSX|MacOS|Unix)\b|OSPlatform\b|OSInformation\.Shared|FileSystem\.Shared\.OS|OperatingSystem\.Is\w+|MatchPlatform|\bon(Windows|Linux|OSX)\s*:")]
    private static partial Regex HostOsCheck();

    [Fact]
    public void Test_NoUnlistedOsChecksInSource()
    {
        var root = FindRepoRoot();
        var allowed = Allowed.Select(a => (a.File, a.Line)).ToHashSet();

        var found = new HashSet<(string File, string Line)>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "obj" or "bin")) continue;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//")) continue;
                if (HostOsCheck().IsMatch(trimmed)) found.Add((relative, trimmed));
            }
        }

        var unlisted = found.Except(allowed).OrderBy(x => x.File).Select(x => $"{x.File}: {x.Line}");
        string.Join(Environment.NewLine, unlisted).Should().BeEmpty("the app only runs on Linux; only checks on the game's target OS belong in src. Collapse the check or add it to the allowlist with a reason");

        var stale = allowed.Except(found).OrderBy(x => x.File).Select(x => $"{x.File}: {x.Line}");
        string.Join(Environment.NewLine, stale).Should().BeEmpty("allowlist entries must match a line in src, remove the ones that no longer do");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NexusMods.App.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find NexusMods.App.sln above the test binaries");
    }
}
