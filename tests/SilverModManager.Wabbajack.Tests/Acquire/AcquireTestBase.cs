using FluentAssertions;
using GameFinder.Wine;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SkyrimSe = SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Networking.Forums;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.Games.CreationEngine;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Tests.Fixtures;
using NSubstitute;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>A zip holding <c>pack/inner.zip</c>, which holds <c>deep/c.txt</c>.</summary>
public sealed record NestedZip(TemporaryPath Outer, byte[] OuterBytes, byte[] InnerBytes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Outer.DisposeAsync();

    public FileIdentity OuterHash => Xx64.Of(OuterBytes);

    public FileIdentity InnerStoreIdentity => FileIdentity.XxHash3(InnerBytes.xxHash3());
}

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
            .WithPrivateSettingsFolder()
            .AddCreationEngine()
            .AddForums()
            .AddWabbajack()
            .OverrideSettingsForTests<WabbajackSettings>(settings => settings with
            {
                InboxPath = new ConfigurablePath(null, TestScratch.Combine($"{Startup.SettingsRoot}/Inbox-{Guid.NewGuid()}")),
                ExtrasRoot = new ConfigurablePath(null, TestScratch.Combine($"{Startup.SettingsRoot}/Extras-{Guid.NewGuid()}")),
                DownloadsFolder = TestScratch.Combine($"{Startup.SettingsRoot}/Downloads-{Guid.NewGuid()}").ToString(),
            })
            .AddUniversalGameLocator<SkyrimSe>(new Version(GameVersion), StubGameFiles, locationsFileSystem: IsolatedPrefixFileSystem.Create);
    }

    protected static readonly RelativePath InnerZip = RelativePath.FromUnsanitizedInput("pack/inner.zip");
    protected static readonly RelativePath InnerMember = RelativePath.FromUnsanitizedInput("deep/c.txt");

    protected async Task<NestedZip> BuildNestedZip()
    {
        await using var inner = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [InnerMember] = Guid.NewGuid().ToByteArray() });
        var innerBytes = await inner.Path.ReadAllBytesAsync();
        var outer = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [InnerZip] = innerBytes });
        return new NestedZip(outer, await outer.Path.ReadAllBytesAsync(), innerBytes);
    }

    /// <summary>
    /// The rows an import leaves when it keeps no bytes for the nested archive: the outer archive and the inner one
    /// as its member, with nothing in the file store for the inner. <paramref name="plain"/> is a further, ordinary
    /// member of the outer archive whose bytes are kept, so a list can still read a file out of the incomplete copy.
    /// </summary>
    protected async Task AddWithoutNestedBytes(NestedZip zip, (RelativePath Path, byte[] Bytes)? plain = null)
    {
        using var tx = Connection.BeginTransaction();
        var outer = NewFile(tx, "outer.zip", zip.OuterBytes);
        var outerArchive = new LibraryArchive.New(tx, outer.Id) { IsArchive = true, LibraryFile = outer };
        var inner = NewFile(tx, "inner.zip", zip.InnerBytes);
        _ = new LibraryArchive.New(tx, inner.Id) { IsArchive = true, LibraryFile = inner };
        _ = new LibraryArchiveFileEntry.New(tx, inner.Id) { Path = InnerZip, LibraryFile = inner, ParentId = outerArchive.Id };
        if (plain is var (path, bytes))
        {
            var member = NewFile(tx, path.FileName.ToString(), bytes);
            _ = new LibraryArchiveFileEntry.New(tx, member.Id) { Path = path, LibraryFile = member, ParentId = outerArchive.Id };
        }
        await tx.Commit();
        if (plain is { } kept)
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(kept.Path, new MemoryStream(kept.Bytes)), FileIdentity.XxHash3(kept.Bytes.xxHash3()), Size.FromLong(kept.Bytes.Length))]);

        (await FileStore.HaveFile(zip.InnerStoreIdentity)).Should().BeFalse("this is how an earlier import left the library");
    }

    private static LibraryFile.New NewFile(ITransaction tx, string name, byte[] bytes)
    {
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        tx.Add(id, LibraryFile.XxHash64, Xx64.Of(bytes).XxHash64Value());
        return file;
    }

    protected static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition never became true");
            await Task.Delay(25);
        }
    }

    protected static FileIdentity Hash64(byte[] bytes) => Xx64.Of(bytes);

    protected static ModList ListWith(params ArchiveEntry[] archives) => WabbajackFixture.EmptyList() with { Archives = archives };

    protected static ArchiveEntry Entry(DownloadState state, FileIdentity? hash = null, long size = 1000, string name = "archive.7z")
        => new(hash ?? Xx64.From(0x1234_5678_9ABC_DEF0), size, name, "", state);
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

    /// <summary>The forum sign-in as the test wants it: a client id the app has, and a token it holds.</summary>
    public static IForumLoginState Forums(bool canSignIn = false, bool signedIn = false)
    {
        var forums = Substitute.For<IForumLoginState>();
        forums.CanSignIn(Arg.Any<ForumSite>()).Returns(canSignIn);
        forums.IsSignedIn(Arg.Any<ForumSite>()).Returns(signedIn);
        return forums;
    }
}

internal static class PrivateSettingsFolder
{
    /// <summary>
    /// Points XDG_DATA_HOME, where the JSON settings backend keeps its files, at a folder of this host's own. The
    /// process-wide folder is shared by every host, so a setting one test saved, or one a parallel test was saving,
    /// was read by the next host.
    /// </summary>
    public static IServiceCollection WithPrivateSettingsFolder(this IServiceCollection services)
    {
        var dataHome = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath(TestScratch.Combine($"{Startup.SettingsRoot}/DataHome-{Guid.NewGuid()}"));
        var overlay = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
            new Dictionary<AbsolutePath, AbsolutePath>(),
            new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = dataHome });
        return services.Replace(ServiceDescriptor.Singleton<IFileSystem>(overlay));
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
