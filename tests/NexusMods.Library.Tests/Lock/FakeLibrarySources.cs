using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Library;
using NexusMods.Library.Lock;
using NexusMods.Library.Sources;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NSubstitute;

namespace NexusMods.Library.Tests.Lock;

/// <summary>
/// A source that serves content from memory and records every call. A download adds a library file whose digest
/// describes what was served, as the real jobs do, so a lock with another digest is a mismatch. Planning rules come
/// from <paramref name="inner"/> when there is one, so the real source's checks stay under test.
/// </summary>
internal sealed class FakeLibrarySource(string name, LockFixtures fixtures, Dictionary<string, string> served, Func<LibrarySourceEntry, string> key, ILibrarySource? inner = null, List<string>? downloads = null) : ILibrarySource
{
    public string Name => name;
    public List<string> Downloads { get; } = downloads ?? [];
    public List<LibrarySourceEntry> Described { get; } = [];
    public HashSet<string> Throwing { get; } = [];

    /// <summary>
    /// Decides which items <see cref="Describe"/> claims; by default none.
    /// </summary>
    public Func<LibraryItem.ReadOnly, LibrarySourceEntry?> Owns { get; set; } = _ => null;

    public ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        var entry = Owns(item);
        if (entry is not null) Described.Add(entry);
        return ValueTask.FromResult(entry);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context) => inner?.WhyNotDownloadable(entry, context);

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        var servedKey = key(entry);
        Downloads.Add(servedKey);
        if (Throwing.Contains(servedKey)) throw new IOException("network down");
        var fileName = servedKey.Contains(':') ? servedKey[(servedKey.IndexOf(':') + 1)..] : servedKey;
        return await fixtures.AddLibraryFile(fileName, served[servedKey], LockFixtures.DigestOf(served[servedKey]));
    }
}

/// <summary>
/// The Nexus Mods, GitHub and Thunderstore sources of the lock tests with their downloads replaced, plus the library service that
/// removes rejected downloads.
/// </summary>
internal sealed class FakeSources
{
    public FakeSources(LockFixtures fixtures, IConnection connection, Dictionary<string, string> served)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var digests = new LibraryFileDigests(Substitute.For<NexusMods.Sdk.FileStore.IFileStore>(), connection);

        Nexus = new FakeLibrarySource(LockSources.Nexus, fixtures, served, entry => $"{entry.FileId}:{entry.Asset}", new NexusLibrarySource(provider, digests), Downloads);
        GitHub = new FakeLibrarySource(LockSources.GitHub, fixtures, served, entry =>
        {
            Repositories.Add(GitHubLibrarySource.FindRepository(connection, entry.Asset) ?? throw new InvalidOperationException("no repository"));
            return entry.Asset;
        }, new GitHubLibrarySource(provider), Downloads);

        Thunderstore = new FakeLibrarySource(LockSources.Thunderstore, fixtures, served, entry => entry.Asset, new ThunderstoreLibrarySource(provider), Downloads);

        LibraryService = Substitute.For<ILibraryService>();
        LibraryService.RemoveLibraryItems(Arg.Any<IEnumerable<LibraryItem.ReadOnly>>(), Arg.Any<GarbageCollectorRunMode>())
            .Returns(call =>
            {
                Discarded.AddRange(call.Arg<IEnumerable<LibraryItem.ReadOnly>>().Select(item => item.Name));
                return Task.CompletedTask;
            });
    }

    public List<string> Downloads { get; } = [];
    public List<string> Discarded { get; } = [];
    public List<GitHubRepositoryRef> Repositories { get; } = [];
    public FakeLibrarySource Nexus { get; }
    public FakeLibrarySource GitHub { get; }
    public FakeLibrarySource Thunderstore { get; }
    public ILibraryService LibraryService { get; }
    public HashSet<string> Throwing => Nexus.Throwing;

    public LockImport CreateImport(IConnection connection) => new(connection, [Nexus, GitHub, Thunderstore], LibraryService);
}
