using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GameBanana;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Abstractions.Games;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The <c>gamebanana</c> verbs over a stubbed game GameBanana lists mods for, with the site answered by a substitute and
/// files served from memory.
/// </summary>
[Collection("ConsoleOutput")]
public class GameBananaVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<GameBananaVerbsTests, GameBananaStubbedGame>(helper)
{
    private const long ModId = 4242;

    // A real archive, because the install step unpacks it; the content is unique so the library keeps a file of its own.
    private readonly byte[] _payload = ZipOf("file.txt", Guid.NewGuid().ToByteArray());
    private readonly IGameBananaApi _api = Substitute.For<IGameBananaApi>();

    private static byte[] ZipOf(string name, byte[] content)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(content);
        }

        return stream.ToArray();
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.Host == "gamebanana.com" && request.RequestUri.AbsolutePath.StartsWith("/dl/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private GameBananaFile FileOf(long id, bool clean = true)
        => new(id, $"mod-{id}.zip", _payload.Length, $"https://gamebanana.com/dl/{id}", Convert.ToHexStringLower(MD5.HashData(_payload)), clean ? "clean" : "infected", 1, 1);

    private GameBananaProfile ProfileOf(params GameBananaFile[] files) => new(
        ModId, "Head HUD", false, false, $"https://gamebanana.com/mods/{ModId}", 10, 10, "1.2", files, null,
        new GameBananaLicenseChecklist(["Download and install this Mod"], [], []), new GameBananaGameRef(297, "Team Fortress 2"), new GameBananaSubmitter("Author"), new GameBananaCategory("HUDs"));

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _api.Profile(ModId, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(ProfileOf(FileOf(70_001))));
        _api.Subfeed(297, 1, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new GameBananaListing(
            [new GameBananaRecord(ModId, "Head HUD", "Mod", $"https://gamebanana.com/mods/{ModId}", 1, 2, 3, true, "1.2", false, 9, 1, new GameBananaSubmitter("Author"), null, new GameBananaGameRef(297, "Team Fortress 2"), new GameBananaCategory("HUDs"))], 1, true)));

        base.AddServices(services)
            .AddGame<GameBananaStubbedGame>()
            .AddUniversalGameLocator<GameBananaStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(new BytesHandler(_payload))));
        services.Replace(ServiceDescriptor.Singleton(_api));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    [Fact]
    public async Task SearchListsTheFrontPageOfTheGame()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("gamebanana", "search", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("Head HUD").And.Contain("Author").And.Contain("1.2").And.Contain(ModId.ToString());
    }

    [Fact]
    public async Task SearchWithAQueryAsksTheSiteSearch()
    {
        var loadout = await CreateLoadout();
        _api.Search(297, "hud", 1, Arg.Any<CancellationToken>()).Returns(Task.FromResult(new GameBananaListing([], 0, true)));

        var (code, output) = await Run("gamebanana", "search", "-l", loadout.Name, "-q", "hud");

        code.Should().Be(0, output);
        output.Should().Contain("No mod in GameBanana matches 'hud'");
    }

    [Fact]
    public async Task InstallTakesTheOnlyFileAndRecordsItsOrigin()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("gamebanana", "install", "-l", loadout.Name, "-m", ModId.ToString());

        code.Should().Be(0, output);
        output.Should().Contain("Head HUD");
        var file = GameBananaLibraryFile.All(Connection.Db).Single(row => row.ModId == ModId);
        (file.FileId, file.GameId).Should().Be((70_001L, 297));
        file.AsLibraryFile().Sha256.Value.ToString().Should().BeEquivalentTo(Convert.ToHexString(SHA256.HashData(_payload)));
    }

    [Fact]
    public async Task AModWithSeveralFilesExitsTwoNamingTheOptionAndFileIdsPickOne()
    {
        var loadout = await CreateLoadout();
        _api.Profile(ModId, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(ProfileOf(FileOf(70_001), FileOf(70_002))));

        var (asked, askedOutput) = await Run("gamebanana", "install", "-l", loadout.Name, "-m", ModId.ToString());
        var (picked, pickedOutput) = await Run("gamebanana", "install", "-l", loadout.Name, "-m", ModId.ToString(), "-f", "70002");

        asked.Should().Be(2, askedOutput);
        askedOutput.Should().Contain("--file").And.Contain("70001").And.Contain("70002");
        picked.Should().Be(0, pickedOutput);
        GameBananaLibraryFile.All(Connection.Db).Should().ContainSingle(row => row.ModId == ModId).Which.FileId.Should().Be(70_002);
    }

    [Fact]
    public async Task AFileTheScanFlaggedExitsTwoWithThePageLink()
    {
        var loadout = await CreateLoadout();
        _api.Profile(ModId, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(ProfileOf(FileOf(70_001, clean: false))));

        var (code, output) = await Run("gamebanana", "install", "-l", loadout.Name, "-m", ModId.ToString());

        code.Should().Be(2, output);
        output.Should().Contain($"https://gamebanana.com/mods/{ModId}");
        GameBananaLibraryFile.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task AModTheGameDoesNotHaveExitsOneNamingTheSearchVerb()
    {
        var loadout = await CreateLoadout();
        _api.Profile(999, Arg.Any<CancellationToken>()).Returns<Task<GameBananaProfile>>(_ => throw new GameBananaUnavailableException(999));

        var (code, output) = await Run("gamebanana", "install", "-l", loadout.Name, "-m", "999");

        code.Should().Be(1, output);
        output.Should().Contain("gamebanana search");
    }
}
