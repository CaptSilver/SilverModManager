using System.Collections.Immutable;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup.SaveToLibrary;

/// <summary>In-memory Nexus Mods: the files of each mod, the mod of a file, and a name search. No network.</summary>
internal sealed class FakeCatalog : INexusCatalog
{
    public Dictionary<uint, List<NexusFile>> Files { get; } = [];
    public Dictionary<uint, uint> ModOfFile { get; } = [];
    public Dictionary<string, List<NexusModHit>> Search { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, uint> MainFile { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<NexusFile>?> ListFilesAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken)
    {
        Calls.Add($"files {modId}");
        return Task.FromResult<IReadOnlyList<NexusFile>?>(Files.TryGetValue(modId, out var files) ? files : null);
    }

    public Task<uint?> ModOfFileAsync(NexusModsGameId game, uint fileId, CancellationToken cancellationToken)
    {
        Calls.Add($"mod-of {fileId}");
        return Task.FromResult(ModOfFile.TryGetValue(fileId, out var mod) ? (uint?)mod : null);
    }

    public Task<IReadOnlyList<NexusModHit>> FindModsByNameAsync(NexusModsGameId game, string name, CancellationToken cancellationToken)
    {
        Calls.Add($"search {name}");
        return Task.FromResult<IReadOnlyList<NexusModHit>>(Search.TryGetValue(name, out var hits) ? hits : []);
    }

    public Task<uint?> FindMainFileAsync(NexusModsGameId game, uint modId, CancellationToken cancellationToken)
    {
        Calls.Add($"main {modId}");
        return Task.FromResult(MainFile.TryGetValue(modId, out var file) ? (uint?)file : null);
    }
}

internal sealed class FakeLibrary : ILibraryProbe
{
    public HashSet<uint> Files { get; } = [];
    public bool Contains(NexusModsGameId game, uint fileId) => Files.Contains(fileId);
}

internal sealed class FakeDownloader(Func<ModToSave, ModDownloadResult>? answer = null) : IModDownloader
{
    public List<string> Log { get; } = [];

    public Task<ModDownloadResult> DownloadAsync(NexusModsGameId game, ModToSave mod, CancellationToken cancellationToken)
    {
        Log.Add($"download {mod.Id}");
        return Task.FromResult(answer?.Invoke(mod) ?? ModDownloadResult.Added);
    }
}

internal sealed class FakePrompts(Func<SavePlan, ImmutableArray<ModToSave>?>? choose = null, Func<SaveReport, bool>? proceedAfterFailures = null, List<string>? log = null) : ISavePrompts
{
    public List<string> Log { get; } = log ?? [];

    public Task<ImmutableArray<ModToSave>?> ChooseAsync(SavePlan plan, CancellationToken cancellationToken)
    {
        Log.Add("choose");
        return Task.FromResult(choose is null ? plan.Save : choose(plan));
    }

    public Task<bool> ContinueAfterFailuresAsync(SaveReport report, CancellationToken cancellationToken)
    {
        Log.Add("failures");
        return Task.FromResult(proceedAfterFailures?.Invoke(report) ?? true);
    }
}
