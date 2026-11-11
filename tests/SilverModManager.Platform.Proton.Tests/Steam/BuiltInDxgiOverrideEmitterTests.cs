using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class BuiltInDxgiOverrideEmitterTests
{
    [Theory]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=dxgi=builtin %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi.dll=b\" %command%")]
    [InlineData("PROTON_LOG=1 WINEDLLOVERRIDES=\"winmm=n,b;dxgi=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b,n\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=B\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=Builtin\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b\" '/home/u/.local/share/silvermod/bin/silvermod' run 'Main' -- %command%")]
    public void ForcingTheBuiltInDxgiIsFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeTrue();
    }

    [Theory]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=n,b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=native,builtin\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"winmm=b\" %command%")]
    [InlineData("WINEDLLOVERRIDES=\"dxgi=b;dxgi=n,b\" %command%")]
    [InlineData("PROTON_USE_WINED3D=1 WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    [InlineData("gamemoderun WINEDLLOVERRIDES=\"dxgi=b\" %command%")]
    public void ADxgiThatFallsBackToDxvkOrIsNotForcedIsNotFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("%command%")]
    [InlineData("PROTON_LOG=1 %command% -windowed")]
    public void ALineWithoutOverridesIsNotFlagged(string line)
    {
        BuiltInDxgiOverrideEmitter.ForcesBuiltInDxgi(line).Should().BeFalse();
    }

    private const string Forced = "WINEDLLOVERRIDES=\"dxgi=b\" %command%";

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);

    private AbsolutePath WriteLocalConfig(string? launchOptions)
    {
        var path = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam/userdata/1/config/localconfig.vdf");
        var entry = launchOptions is null ? "" : $"\"LaunchOptions\" \"{launchOptions.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        _fs.CreateDirectory(path.Parent);
        _fs.WriteAllText(path, $$"""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" { "42" { {{entry}} } } } } }
            }
            """);
        return path;
    }

    private GameInstallation Install(IGameData game, AbsolutePath? localConfig, bool withProvider = true)
    {
        game.DisplayName.Returns("Test Game");
        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.SteamLocalConfigPath.Returns(localConfig ?? _fs.FromUnsanitizedFullPath("/home/u/.steam/steam/userdata/1/config/localconfig.vdf"));

        var folder = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam/steamapps/common/Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            LinuxCompatabilityDataProvider = withProvider ? provider : null,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private static IGameData DxgiGame() => Substitute.For<IGameData, IDxgiGame>();

    [Fact]
    public void ADxgiGameWithTheOverrideForcedIsReported()
    {
        var config = WriteLocalConfig(Forced);

        var diagnostic = BuiltInDxgiOverrideEmitter.Evaluate(Install(DxgiGame(), config));

        diagnostic.Should().NotBeNull();
        diagnostic!.Summary.Value.Should().Contain("{GameName}");
    }

    [Fact]
    public void AGameThatNeverLoadsDxgiIsNotReportedForTheSameLine()
    {
        var config = WriteLocalConfig(Forced);

        // A DirectX 9 or older title such as Oblivion implements no IDxgiGame.
        BuiltInDxgiOverrideEmitter.Evaluate(Install(Substitute.For<IGameData>(), config)).Should().BeNull();
    }

    [Fact]
    public void ADxgiGameWithoutTheOverrideIsNotReported()
    {
        var config = WriteLocalConfig("PROTON_LOG=1 %command%");

        BuiltInDxgiOverrideEmitter.Evaluate(Install(DxgiGame(), config)).Should().BeNull();
    }

    [Fact]
    public void AMissingLocalConfigIsNotReported()
    {
        BuiltInDxgiOverrideEmitter.Evaluate(Install(DxgiGame(), null)).Should().BeNull();
    }

    [Fact]
    public void AnUnreadableLocalConfigIsNotReported()
    {
        var config = WriteLocalConfig(null);
        _fs.WriteAllText(config, "\"UserLocalConfigStore\" { \"Software\" { ");

        BuiltInDxgiOverrideEmitter.Evaluate(Install(DxgiGame(), config)).Should().BeNull();
    }

    [Fact]
    public void AGameWithoutKnownSteamDataIsNotReported()
    {
        var config = WriteLocalConfig(Forced);

        BuiltInDxgiOverrideEmitter.Evaluate(Install(DxgiGame(), config, withProvider: false)).Should().BeNull();
    }
}
