using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModIo;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.Abstractions.Games;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The <c>modio</c> verbs over a stubbed game mod.io hosts mods for, with the service answered by a substitute and files
/// served from memory.
/// </summary>
[Collection("ConsoleOutput")]
public class ModIoVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<ModIoVerbsTests, ModIoStubbedGame>(helper)
{
    private const uint ModIoGame = 4242;
    private const uint ModId = 5151;
    private const string BinaryUrl = "https://files.test/5151/mod.zip";

    // A real archive, because the install step unpacks it; the content is unique so the library keeps a file of its own.
    private readonly byte[] _payload = ZipOf("file.txt", Guid.NewGuid().ToByteArray());
    private readonly IModIoApi _api = Substitute.For<IModIoApi>();

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
            => Task.FromResult(request.RequestUri!.ToString() == BinaryUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private ModIoMod ModOf(bool clean = true) => new(
        ModId, "Wider Camera", "Pulls the camera back.", $"https://mod.io/g/stubbed/m/wider-camera", 10, 0, null, new ModIoUserRef("Camille"),
        new ModIoFile(9001, ModId, 5, _payload.Length, new ModIoFileHash(Convert.ToHexStringLower(MD5.HashData(_payload))), "mod.zip", "1.2", new ModIoDownload(BinaryUrl, 9_999_999_999), 1, clean ? 0 : 1),
        new ModIoStats(77));

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _api.Mod(ModIoGame, ModId, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ModIoMod?>(ModOf()));
        _api.Mods(ModIoGame, Arg.Any<ModIoQuery>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new ModIoPage<ModIoMod>([ModOf()], 1, 0)));

        base.AddServices(services)
            .AddGame<ModIoStubbedGame>()
            .AddUniversalGameLocator<ModIoStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(ThunderstoreVerbFixtures.BuildRoutes());
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(new BytesHandler(_payload))));
        services.Replace(ServiceDescriptor.Singleton(_api));
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    [Fact]
    public async Task SearchListsTheGamesModsFromModIo()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("modio", "search", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("Wider Camera").And.Contain("Camille").And.Contain("1.2").And.Contain(ModId.ToString());
    }

    [Fact]
    public async Task InstallDownloadsTheModfileAndRecordsItsOrigin()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("modio", "install", "-l", loadout.Name, "-m", ModId.ToString());

        code.Should().Be(0, output);
        var file = ModIoLibraryFile.All(Connection.Db).Single(row => row.ModId == ModId);
        (file.FileId, file.GameId, file.Version).Should().Be((9001u, ModIoGame, "1.2"));
        file.AsLibraryFile().Sha256.Value.ToString().Should().BeEquivalentTo(Convert.ToHexString(SHA256.HashData(_payload)));
    }

    [Fact]
    public async Task WithoutATokenInstallExitsTwoNamingTheSettingsRow()
    {
        var loadout = await CreateLoadout();
        _api.Mod(ModIoGame, ModId, Arg.Any<CancellationToken>()).Returns<Task<ModIoMod?>>(_ => throw new ModIoNotSignedInException());

        var (code, output) = await Run("modio", "install", "-l", loadout.Name, "-m", ModId.ToString());

        code.Should().Be(2, output);
        output.Should().Contain("Settings").And.Contain("mod.io");
        ModIoLibraryFile.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task AModTheGameDoesNotHaveExitsOneNamingTheSearchVerb()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("modio", "install", "-l", loadout.Name, "-m", "999");

        code.Should().Be(1, output);
        output.Should().Contain("modio search");
    }

    [Fact]
    public async Task UpdatesSaysSoWhenNoHeldModHasOne()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("modio", "updates", "-l", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("No mod.io mod has an update");
    }
}
