using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Library;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Format;
using NexusMods.Wabbajack.Install;
using NexusMods.Wabbajack.Models;
using NexusMods.Wabbajack.Tests.Acquire;
using NexusMods.Wabbajack.Tests.Fixtures;

namespace NexusMods.Wabbajack.Tests.Install;

/// <summary>
/// A stubbed Skyrim SE install with a game executable, no Wine prefix for it, and helpers that put archives and
/// lists in the library and run the install job against them.
/// </summary>
public abstract class InstallTestBase<TTest>(ITestOutputHelper helper) : AcquireTestBase<TTest>(helper)
    where TTest : class
{
    protected static readonly byte[] StubExe = Enumerable.Range(0, 500).Select(i => (byte)(i * 3)).ToArray();

    protected override Dictionary<RelativePath, byte[]> StubGameFiles => new()
    {
        [GameBsa] = GameBsaBytes,
        [RelativePath.FromUnsanitizedInput("SkyrimSE.exe")] = StubExe,
    };

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // The stub game has no Proton prefix, so a remapped path comes out as a Z: path.
        services.AddSingleton(provider => new FakePrefixResolver(provider.GetRequiredService<TemporaryFileManager>())
        {
            Resolution = _ => PrefixResolution.Failed("no prefix in tests"),
        });
        return base.AddServices(services).AddFakeToolRunner();
    }

    protected static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    /// <summary>Puts a zip with these files in the library and returns the list entry that names it by hash.</summary>
    protected async Task<ArchiveEntry> AddZip(string name, Dictionary<string, byte[]> files)
    {
        await using var zip = await CreateTestArchive(files.ToDictionary(f => P(f.Key), f => f.Value));
        var library = await RegisterLocalArchive(zip.Path);
        var file = library.AsLibraryFile();
        return new ArchiveEntry(file.XxHash64.Value, (long)file.Size.Value, name, "", new HttpState(new Uri($"https://example.invalid/{name}"), []));
    }

    /// <summary>The entry for the stub game's own executable, as a list made for this game version would carry it.</summary>
    protected static ArchiveEntry GameExeArchive()
        => new(Hash64(StubExe), StubExe.Length, "SkyrimSE.exe", "", new GameFileSourceState("SkyrimSpecialEdition", P("SkyrimSE.exe"), Hash64(StubExe), GameVersion));

    protected async Task<WabbajackModlistLibraryFile.ReadOnly> AddModlist(ModList list, IReadOnlyDictionary<string, byte[]> blobs)
    {
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, list, blobs, $"{Guid.NewGuid()}.wabbajack");
        var added = await LibraryService.AddLocalFile(path);
        if (!added.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out var modlist))
            throw new InvalidOperationException("The fixture was not recognised as a modlist.");
        return modlist;
    }

    protected async Task<(InstallModlistJob Job, WabbajackModlistLoadoutGroup.ReadOnly Group)> Install(
        Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly modlist, InstallOptions options)
    {
        var task = InstallModlistJob.Create(ServiceProvider, loadout.LoadoutId, modlist, options);
        var group = await task;
        return (task.JobDefinition, group);
    }

    /// <summary>The extras folder recorded on the group, read fresh because it is recorded after the group is created.</summary>
    protected AbsolutePath ExtrasOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => ServiceProvider.GetRequiredService<IFileSystem>().FromUnsanitizedFullPath(WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).ExtrasFolder.Value);

    /// <summary>Group name to (target path to hash) for each mod group of the list, as the database holds them now.</summary>
    protected Dictionary<string, Dictionary<string, Hash>> TreeOf(WabbajackModlistLoadoutGroup.ReadOnly group)
    {
        var db = Connection.Db;
        var tree = new Dictionary<string, Dictionary<string, Hash>>();
        foreach (var child in LoadoutItem.FindByParent(db, group.Id).OfTypeLoadoutItemGroup())
        {
            var files = new Dictionary<string, Hash>();
            foreach (var file in LoadoutItem.FindByParent(db, child.Id).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
            {
                GamePath path = file.AsLoadoutItemWithTargetPath().TargetPath;
                files[$"{path.LocationId}/{path.Path}"] = file.Hash;
            }
            tree[child.AsLoadoutItem().Name] = files;
        }
        return tree;
    }

    protected IReadOnlyList<WabbajackSkippedFile.ReadOnly> SkipsOf(WabbajackModlistLoadoutGroup.ReadOnly group)
        => WabbajackSkippedFile.FindByGroup(Connection.Db, group.Id).ToArray();

    protected static async Task<byte[]> ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

/// <summary>Collects the pieces of a list: archives, directives and the blobs the directives name.</summary>
public sealed class ListBuilder
{
    public List<ArchiveEntry> Archives { get; } = [];
    public List<Directive> Directives { get; } = [];
    public Dictionary<string, byte[]> Blobs { get; } = new();

    private static RelativePath P(string path) => RelativePath.FromUnsanitizedInput(path);

    public string Blob(byte[] bytes)
    {
        var id = Guid.NewGuid().ToString();
        Blobs[id] = bytes;
        return id;
    }

    public FromArchive Member(string to, ArchiveEntry archive, string part, byte[] bytes)
        => new(P(to), Xx(bytes), bytes.Length, new ArchiveHashPath(archive.XxHash64, [P(part)]));

    public InlineFile Inline(string to, string text) => Inline(to, System.Text.Encoding.UTF8.GetBytes(text));

    public InlineFile Inline(string to, byte[] bytes) => new(P(to), Xx(bytes), bytes.Length, Blob(bytes));

    /// <summary>A file whose markers are replaced on install; its recorded hash is of the text as the compiler wrote it.</summary>
    public RemappedInlineFile Remapped(string to, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new RemappedInlineFile(P(to), Xx(bytes), bytes.Length, Blob(bytes));
    }

    public ArchiveMeta Meta(string to, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new ArchiveMeta(P(to), Xx(bytes), bytes.Length, Blob(bytes));
    }

    public ModList ToList(string name = "Test list") => WabbajackFixture.EmptyList(name) with
    {
        Archives = Archives.ToArray(),
        Directives = Directives.ToArray(),
    };

    private static NexusMods.Hashing.xxHash3.Hash Xx(byte[] bytes) => NexusMods.Sdk.Hashes.Xx64Hasher.Hash(bytes);
}
