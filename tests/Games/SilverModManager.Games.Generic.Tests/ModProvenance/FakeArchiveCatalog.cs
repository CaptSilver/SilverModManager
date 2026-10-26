using SilverModManager.Games.Generic.ModProvenance;

namespace SilverModManager.Games.Generic.Tests.ModProvenance;

internal sealed class FakeArchiveCatalog : IArchiveCatalog
{
    public Dictionary<string, ArchiveHit> Hits { get; } = [];
    public Dictionary<(uint Mod, uint File), List<ArchiveEntry>> Content { get; } = [];
    public List<string[]> Looked { get; } = [];
    public List<string> ContentCalls { get; } = [];
    public bool Fail { get; init; }
    public bool Cancel { get; init; }

    public Task<IReadOnlyList<ArchiveHit>> LookupArchivesAsync(IReadOnlyList<string> md5s, CancellationToken cancellationToken)
    {
        Looked.Add([..md5s]);
        if (Cancel) throw new OperationCanceledException();
        if (Fail) throw new InvalidOperationException("boom");
        return Task.FromResult<IReadOnlyList<ArchiveHit>>(md5s.Where(Hits.ContainsKey).Select(h => Hits[h]).ToArray());
    }

    public Task<IReadOnlyList<ArchiveEntry>?> ListArchiveContentAsync(uint game, uint modId, uint fileId, CancellationToken cancellationToken)
    {
        ContentCalls.Add($"{modId}/{fileId}");
        if (Fail) throw new InvalidOperationException("boom");
        return Task.FromResult<IReadOnlyList<ArchiveEntry>?>(Content.TryGetValue((modId, fileId), out var entries) ? entries : null);
    }
}
