using FluentAssertions;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.RedEngine.Cyberpunk2077.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.RedEngine.Tests;

/// <summary>
/// The pre-launch warning that Cyberpunk was updated after RED4ext or Cyber Engine Tweaks last ran. The
/// fixtures copy the shapes of the extenders' logs and a game executable into a temp folder; nothing here
/// touches a real game or Steam folder.
/// </summary>
public sealed class GameUpdatedSinceExtenderRanCheckTests : IDisposable
{
    private const string OldVersion = "3.0.80.51928";
    private const string NewVersion = "3.0.88.46235";

    private readonly string _root = TestScratch.Combine("extender-ran-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _logger = new();

    public GameUpdatedSinceExtenderRanCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Check_ExeIsNewerThanBothLogs_WarnsBeforeAnyLaunch()
    {
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"), ("Codeware", "1.20.5"), ("audioware", "1.9.9-rc.0"));
        WriteCetLog(OldVersion);

        var diagnostic = Check().Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameUpdatedSinceExtenderRanMessageData>>().Subject.MessageData;
        data.Game.Should().Be("Cyberpunk 2077");
        data.GameVersion.Should().Be(NewVersion);
        data.LoggedVersion.Should().Be(OldVersion);
        data.Extender.Should().Be("RED4ext and Cyber Engine Tweaks");
        data.Plugins.Should().Contain("ArchiveXL 1.27.4").And.Contain("Codeware 1.20.5").And.Contain("audioware 1.9.9-rc.0");
    }

    [Fact]
    public void Check_BothLogsMatchTheExe_IsSilent()
    {
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-24-10-00-00", NewVersion, ("ArchiveXL", "1.28.0"));
        WriteCetLog(NewVersion);

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_OnlyRed4ExtIsStale_NamesOnlyRed4Ext()
    {
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"));
        WriteCetLog(NewVersion);

        var data = Check().Should().ContainSingle().Subject.Should().BeOfType<Diagnostic<Diagnostics.GameUpdatedSinceExtenderRanMessageData>>().Subject.MessageData;

        data.Extender.Should().Be("RED4ext");
        data.LoggedVersion.Should().Be(OldVersion);
    }

    [Fact]
    public void Check_OnlyCetIsStale_NamesOnlyCetAndListsNoPlugins()
    {
        WriteExe(NewVersion);
        WriteCetLog(OldVersion);

        var data = Check().Should().ContainSingle().Subject.Should().BeOfType<Diagnostic<Diagnostics.GameUpdatedSinceExtenderRanMessageData>>().Subject.MessageData;

        data.Extender.Should().Be("Cyber Engine Tweaks");
        data.Plugins.Should().BeEmpty();
    }

    [Fact]
    public void Check_ExtendersLoggedDifferentOldVersions_NamesEachExtendersVersion()
    {
        // The two extenders last ran at different times, so each may have seen a different old build.
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"));
        WriteCetLog("3.0.70.12345");

        var data = Check().Should().ContainSingle().Subject.Should().BeOfType<Diagnostic<Diagnostics.GameUpdatedSinceExtenderRanMessageData>>().Subject.MessageData;

        data.Extender.Should().Be("RED4ext and Cyber Engine Tweaks");
        data.LoggedVersion.Should().Be($"{OldVersion} (RED4ext) and 3.0.70.12345 (Cyber Engine Tweaks)");
    }

    [Fact]
    public void Check_CetLogIsAppendedAcrossLaunches_ReadsTheLastGameVersion()
    {
        // The old launches stay in the file; only the newest one says what CET last saw.
        WriteExe(NewVersion);
        WriteCetLog(OldVersion, NewVersion);

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_RedExtLogsAreOnePerLaunch_ReadsTheNewestOne()
    {
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-20-23-46-00", OldVersion, ("ArchiveXL", "1.27.4"));
        WriteRed4ExtLog("2026-10-24-12-00-00", NewVersion, ("ArchiveXL", "1.28.0"));

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_ExtenderHasNeverRun_IsSilent()
    {
        // No log means no prior launch with an extender, so there is no version to compare with.
        WriteExe(NewVersion);

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_LogsHoldNoGameVersion_IsSilent()
    {
        WriteExe(NewVersion);
        WriteFile("red4ext/logs/red4ext-2026-10-21-23-47-21.log", "[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] RED4ext (v1.30.0) is initializing...\n");
        WriteFile("bin/x64/plugins/cyber_engine_tweaks/cyber_engine_tweaks.log", "");

        Check().Should().BeEmpty();
    }

    [Fact]
    public void Check_ExeIsMissing_IsSilentAndTraces()
    {
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"));

        Check().Should().BeEmpty();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Trace);
    }

    [Fact]
    public void Check_ExeHasNoVersionResource_IsSilentAndTraces()
    {
        WriteFile("bin/x64/Cyberpunk2077.exe", new byte[256]);
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"));

        Check().Should().BeEmpty();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Trace);
    }

    [Fact]
    public void Check_OtherGame_IsSilent()
    {
        WriteExe(NewVersion);
        WriteRed4ExtLog("2026-10-21-23-47-21", OldVersion, ("ArchiveXL", "1.27.4"));

        Check(GameId.From("Test.OtherGame")).Should().BeEmpty();
    }

    [Fact]
    public void Cyberpunk2077_SaysAnUpdateAffectsItsMods()
    {
        typeof(IGameUpdateSensitivity).IsAssignableFrom(typeof(Cyberpunk2077Game)).Should().BeTrue();
    }

    [Fact]
    public void Red4ExtLog_ReadsTheFileVersionAndEveryLoadedPlugin()
    {
        var log = Red4ExtLog.Parse([
            "[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] RED4ext (v1.30.0) is initializing...",
            "[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] Product version: 2.31",
            "[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] File version: 3.0.80.51928",
            "[2026-10-21 23:47:27.891] [info    ] [   364] [RED4ext] Loading plugin from 'S:\\common\\x\\acu_rs.dll'...",
            "[2026-10-21 23:47:27.895] [info    ] [   364] [RED4ext] acu-rs (version: 3.2.1, author(s): Potato-of-Doom) has been loaded",
            "[2026-10-21 23:47:29.280] [info    ] [   364] [RED4ext] Immersive First Person (version: 0.2.0, author(s): A, B) has been loaded",
            "[2026-10-21 23:47:29.300] [error   ] [   364] [RED4ext] Broken (version: 1.0.0, author(s): X) could not be loaded",
        ]);

        log.FileVersion.Should().Be("3.0.80.51928");
        log.Plugins.Should().Equal(new RedPlugin("acu-rs", "3.2.1"), new RedPlugin("Immersive First Person", "0.2.0"));
    }

    [Fact]
    public void Red4ExtLog_NoVersionLine_HasNoFileVersion()
    {
        Red4ExtLog.Parse(["[RED4ext] RED4ext (v1.30.0) is initializing..."]).FileVersion.Should().BeNull();
    }

    [Fact]
    public void CetLog_TakesTheLastGameVersionLine()
    {
        CetLog.LastGameVersion([
            "[2026-10-20 23:59:34 UTC-05:00] [info] [f] [1132] CET version v1.37.1 [HEAD]",
            "[2026-10-20 23:59:34 UTC-05:00] [info] [f] [1132] Game version 3.0.80.51928",
            "[2026-10-21 23:47:21 UTC-05:00] [info] [f] [364] Game version 3.0.88.46235",
            "[2026-10-21 23:47:22 UTC-05:00] [info] [f] [364] Root path: \"S:\\Game version 9\"",
        ]).Should().Be("3.0.88.46235");
    }

    private IEnumerable<Diagnostic> Check(GameId? gameId = null)
    {
        var game = Substitute.For<SubstitutableGameData>();
        game.GameId = gameId ?? Cyberpunk2077Game.GameId;
        game.DisplayName.Returns("Cyberpunk 2077");

        var locator = Substitute.For<IGameLocator>();
        var path = FileSystem.Shared.FromUnsanitizedFullPath(_root);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = path,
            Store = GameStore.Steam,
            StoreIdentifier = "1091500",
            LocatorIds = [],
            Locator = locator,
        };
        var installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, path)));
        var context = new GameUpdateContext(default, installation, Substitute.For<IGameUpdateSensitivity>(), result);

        return new GameUpdatedSinceExtenderRanCheck(_logger).Check(context).ToArray();
    }

    private void WriteExe(string fileVersion) => WriteFile("bin/x64/Cyberpunk2077.exe", FixedFileInfo(Version.Parse(fileVersion)));

    private void WriteRed4ExtLog(string stamp, string gameVersion, params (string Name, string Version)[] plugins)
    {
        var text = new StringBuilder();
        text.AppendLine("[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] RED4ext (v1.30.0) is initializing...");
        text.AppendLine("[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] Product version: 2.31");
        text.AppendLine($"[2026-10-21 23:47:21.740] [info    ] [   364] [RED4ext] File version: {gameVersion}");
        foreach (var (name, version) in plugins)
            text.AppendLine($"[2026-10-21 23:47:27.895] [info    ] [   364] [RED4ext] {name} (version: {version}, author(s): someone) has been loaded");
        WriteFile($"red4ext/logs/red4ext-{stamp}.log", text.ToString());
    }

    /// <summary>One launch per version, oldest first, in the one file CET appends to.</summary>
    private void WriteCetLog(params string[] gameVersions)
    {
        var text = new StringBuilder();
        foreach (var version in gameVersions)
        {
            text.AppendLine("[2026-10-20 23:59:34 UTC-05:00] [info] [__cdecl Options::Options(struct Paths &)] [1132] CET version v1.37.1 [HEAD]");
            text.AppendLine($"[2026-10-20 23:59:34 UTC-05:00] [info] [__cdecl Options::Options(struct Paths &)] [1132] Game version {version}");
            text.AppendLine("[2026-10-20 23:59:34 UTC-05:00] [info] [__cdecl Options::Options(struct Paths &)] [1132] Root path: \"S:\\common\\Cyberpunk 2077\\bin\\x64\"");
        }
        WriteFile("bin/x64/plugins/cyber_engine_tweaks/cyber_engine_tweaks.log", text.ToString());
    }

    private void WriteFile(string relative, string text) => WriteFile(relative, Encoding.UTF8.GetBytes(text));

    private void WriteFile(string relative, byte[] bytes)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    /// The 52-byte VS_FIXEDFILEINFO a PE's version resource holds, between padding. Enough for the reader,
    /// which scans for the struct instead of walking the resource directory.
    /// </summary>
    private static byte[] FixedFileInfo(Version version)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[512]);
        writer.Write(0xFEEF04BDu);
        writer.Write(0x00010000u);
        writer.Write((uint)(version.Major << 16 | version.Minor));
        writer.Write((uint)(version.Build << 16 | version.Revision));
        writer.Write(new byte[512]);
        return stream.ToArray();
    }

    /// <summary>
    /// <see cref="IGameData"/> with a plain <see cref="GameId"/>: NSubstitute builds a default value for an
    /// unconfigured property first, and a game id has no default.
    /// </summary>
    public abstract class SubstitutableGameData : IGameData
    {
        public GameId GameId { get; set; }
        public abstract StoreIdentifiers StoreIdentifiers { get; }
        public abstract string DisplayName { get; }
        public abstract DynamicData.Kernel.Optional<SilverModManager.Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; }
        public abstract SilverModManager.Sdk.IO.IStreamFactory IconImage { get; }
        public abstract SilverModManager.Sdk.IO.IStreamFactory TileImage { get; }
        public abstract ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult);
        public abstract GamePath GetPrimaryFile(GameInstallation installation);
    }

    private sealed class CapturingLogger : ILogger<GameUpdatedSinceExtenderRanCheck>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
