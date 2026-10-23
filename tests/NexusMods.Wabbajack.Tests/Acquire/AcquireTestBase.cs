using GameFinder.Wine;
using SkyrimSe = NexusMods.Games.CreationEngine.SkyrimSE.SkyrimSE;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.Games.CreationEngine;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Tests.Fixtures;
using NSubstitute;

namespace NexusMods.Wabbajack.Tests.Acquire;

/// <summary>
/// A stubbed Skyrim SE install (game files from <see cref="StubGameFiles"/>, Documents in a throwaway prefix) with the
/// Wabbajack services registered. Nothing here reaches the network or the real Steam folders.
/// </summary>
public abstract class AcquireTestBase<TTest>(ITestOutputHelper helper)
    : AIsolatedGameTest<TTest, SkyrimSe>(helper)
    where TTest : class
{
    protected static readonly RelativePath GameBsa = RelativePath.FromUnsanitizedInput("Data/Skyrim - Test.bsa");
    protected static readonly byte[] GameBsaBytes = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
    protected const string GameVersion = "1.6.1170";

    protected virtual Dictionary<RelativePath, byte[]> StubGameFiles => new() { [GameBsa] = GameBsaBytes };

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddWabbajack()
            .OverrideSettingsForTests<WabbajackSettings>(settings => settings with
            {
                InboxPath = new ConfigurablePath(KnownPath.EntryDirectory, $"{Startup.SettingsRoot}/Inbox-{Guid.NewGuid()}"),
                ExtrasRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"{Startup.SettingsRoot}/Extras-{Guid.NewGuid()}"),
            })
            .AddUniversalGameLocator<SkyrimSe>(new Version(GameVersion), StubGameFiles, locationsFileSystem: IsolatedPrefixFileSystem.Create);
    }

    protected static Hash Hash64(byte[] bytes) => Xx64Hasher.Hash(bytes);

    protected static ModList ListWith(params ArchiveEntry[] archives) => WabbajackFixture.EmptyList() with { Archives = archives };

    protected static ArchiveEntry Entry(DownloadState state, Hash? hash = null, long size = 1000, string name = "archive.7z")
        => new(hash ?? Hash.From(0x1234_5678_9ABC_DEF0), size, name, "", state);
}

/// <summary>Domain-to-game-id table for tests; Skyrim LE and SSE have different ids.</summary>
internal sealed class FakeMappingCache : IGameDomainToGameIdMappingCache
{
    public static readonly NexusModsGameId SkyrimLe = NexusModsGameId.From(110);
    public static readonly NexusModsGameId SkyrimSe = NexusModsGameId.From(1704);

    private readonly Dictionary<string, NexusModsGameId> _ids = new()
    {
        ["skyrim"] = SkyrimLe,
        ["skyrimspecialedition"] = SkyrimSe,
    };

    public GameDomain GetDomain(NexusModsGameId id) => GameDomain.From(_ids.First(p => p.Value == id).Key);

    public NexusModsGameId GetId(GameDomain domain) => _ids[domain.ToString()];
}

internal static class Fakes
{
    public static ILoginManager Login(bool premium)
    {
        var login = Substitute.For<ILoginManager>();
        login.IsPremium.Returns(premium);
        return login;
    }
}

/// <summary>A game-folder file system whose Documents and AppData sit in a throwaway Wine prefix.</summary>
internal static class IsolatedPrefixFileSystem
{
    public static IFileSystem Create(IServiceProvider provider)
    {
        var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
        var overlay = new WinePrefix { ConfigurationDirectory = pfx.Path }.CreateOverlayFileSystem(NexusMods.Paths.FileSystem.Shared);
        overlay.GetKnownPath(KnownPath.MyDocumentsDirectory).CreateDirectory();
        overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).CreateDirectory();
        return overlay;
    }
}
