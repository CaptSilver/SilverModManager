using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Steam;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Abstractions.Cli;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>steam fetch-file</c> over a substitute Steam session that streams known bytes. Nothing here reaches Steam or the
/// keyring: the session is replaced, so no login is ever started.
/// </summary>
[Collection("ConsoleOutput")]
public class SteamFetchFileVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<SteamFetchFileVerbTests, GameBananaStubbedGame>(helper)
{
    private const uint App = 262060;
    private const uint Depot = 262061;
    private const ulong Manifest = 7_000_000_000_002;

    private readonly byte[] _payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 2000).Select(i => (byte)(i * 3 + 7))).ToArray();
    private readonly string _fileName = $"file-{Guid.NewGuid():N}.dat";
    private readonly ISteamSession _session = Substitute.For<ISteamSession>();

    private RelativePath PathInDepot => RelativePath.FromUnsanitizedInput($"Data/{_fileName}");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var manifest = new Manifest
        {
            ManifestId = ManifestId.From(Manifest),
            DepotId = DepotId.From(Depot),
            CreationTime = DateTimeOffset.UnixEpoch,
            TotalCompressedSize = Size.Zero,
            TotalUncompressedSize = Size.FromLong(_payload.Length),
            Files = [new Manifest.FileData { Path = PathInDepot, Size = Size.FromLong(_payload.Length), Hash = Sha1Value.From(SHA1.HashData(_payload)), Chunks = [] }],
        };
        _session.GetManifestContents(AppId.From(App), DepotId.From(Depot), ManifestId.From(Manifest), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(manifest);
        _session.GetFileStream(AppId.From(App), manifest, PathInDepot).Returns(_ => new MemoryStream(_payload));

        base.AddServices(services)
            .AddGame<GameBananaStubbedGame>()
            .AddUniversalGameLocator<GameBananaStubbedGame>(Version.Parse("0.0.0"))
            .AddSteamCli()
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton(_session));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    [Fact]
    public async Task TheFileIsAddedToTheLibraryAndItsHashesAreNamed()
    {
        var (code, output) = await Run("steam", "fetch-file", "-a", App.ToString(), "-d", Depot.ToString(), "-m", Manifest.ToString(), "-p", PathInDepot.ToString());

        code.Should().Be(0, output);
        output.Should().Contain(_fileName).And.Contain(Convert.ToHexStringLower(SHA256.HashData(_payload)));
        var added = SteamDepotLibraryFile.All(Connection.Db).Single(file => file.Path == PathInDepot.ToString());
        (added.AppId, added.DepotId, added.ManifestId).Should().Be((App, Depot, Manifest));
    }

    [Fact]
    public async Task TheLoginIsMadeOnTheCallingTerminal()
    {
        await Run("steam", "fetch-file", "-a", App.ToString(), "-d", Depot.ToString(), "-m", Manifest.ToString(), "-p", PathInDepot.ToString());

        await _session.Received(1).Connect(Arg.Is<IAuthInterventionHandler>(handler => handler is Networking.Steam.CLI.RenderingAuthenticationHandler), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFileTheManifestDoesNotListExitsOneInWords()
    {
        var (code, output) = await Run("steam", "fetch-file", "-a", App.ToString(), "-d", Depot.ToString(), "-m", Manifest.ToString(), "-p", "Data/not-there.dat");

        code.Should().Be(1, output);
        output.Should().Contain("not-there.dat");
        SteamDepotLibraryFile.All(Connection.Db).Should().NotContain(file => file.Path.EndsWith("not-there.dat"));
    }

    [Fact]
    public async Task ANonNumericManifestIdIsRefusedBeforeAnyLogin()
    {
        var (code, output) = await Run("steam", "fetch-file", "-a", App.ToString(), "-d", Depot.ToString(), "-m", "latest", "-p", PathInDepot.ToString());

        code.Should().Be(1, output);
        output.Should().Contain("manifest id");
        await _session.DidNotReceiveWithAnyArgs().Connect(default(CancellationToken));
        await _session.DidNotReceiveWithAnyArgs().Connect(default(IAuthInterventionHandler)!, default);
    }
}
