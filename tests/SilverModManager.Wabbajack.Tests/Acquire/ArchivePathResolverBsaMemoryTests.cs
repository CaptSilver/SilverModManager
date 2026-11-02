using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// Resolves thousands of members out of one Bethesda archive, as a list that draws its textures and meshes from
/// the game's own archives and from mod archives does, and measures what the resolver keeps afterwards.
/// </summary>
public class ArchivePathResolverBsaMemoryTests(ITestOutputHelper helper) : AcquireTestBase<ArchivePathResolverBsaMemoryTests>(helper)
{
    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    private static readonly int ArchiveMembers = Env("SILVER_BSA_MEMBERS", 12000);
    private static readonly int MemberBytes = Env("SILVER_BSA_MEMBER_BYTES", 2048);

    private static readonly byte[] BigBsa = BuildBsa();

    protected override Dictionary<RelativePath, byte[]> StubGameFiles => new() { [GameBsa] = BigBsa };

    private static string NameOf(int i) => $"textures/actors/character/set{i % 40}/part{i / 40}.dds";

    private static byte[] MemberData(int i)
    {
        var bytes = new byte[MemberBytes];
        new Random(i).NextBytes(bytes);
        return bytes;
    }

    private static byte[] BuildBsa()
    {
        using var temp = new TemporaryFileManager(NexusMods.Paths.FileSystem.Shared);
        var builder = BethesdaArchiveBuilder.Create(new Tes5ArchiveState("BSA\0", 104, 0x3, 0), temp);
        for (var i = 0; i < ArchiveMembers; i++)
            builder.AddFile(new Tes5FileState(i, RelativePath.FromUnsanitizedInput(NameOf(i)), false), new MemoryStream(MemberData(i)), CancellationToken.None).GetAwaiter().GetResult();
        var output = new MemoryStream();
        builder.Build(output, CancellationToken.None).GetAwaiter().GetResult();
        builder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static long Retained()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(true);
    }

    private async Task<(long Retained, long Peak, long PeakWorkingSet)> ResolveMembers(ArchivePathResolver resolver, FileIdentity archive, int count, RelativePath[] via)
    {
        var before = Retained();
        long peak = 0, peakWs = 0;
        var process = Process.GetCurrentProcess();
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                process.Refresh();
                peakWs = Math.Max(peakWs, process.WorkingSet64);
                try { await timer.WaitForNextTickAsync(stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var gate = new SemaphoreSlim(Env("SILVER_BSA_WORKERS", 24));
        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync();
            try { await resolver.Resolve(new ArchiveHashPath(archive, [.. via, RelativePath.FromUnsanitizedInput(NameOf(i))]), CancellationToken.None); }
            finally { gate.Release(); }
        }));
        await stop.CancelAsync();
        await sampler;
        return (Retained() - before, peak - before, peakWs);
    }

    private ArchivePathResolver NewResolver(ArchivePlan plan, SilverModManager.Sdk.Loadouts.Loadout.ReadOnly loadout)
        => new(plan, Connection.Db, FileStore,
            new GameFileSourceLookup(loadout, Connection, FileStore, ServiceProvider.GetRequiredService<IFileIdentityResolver>(), ServiceProvider.GetRequiredService<IFileHashesService>()),
            ServiceProvider.GetServices<INestedArchiveOpener>(), new ListLogger<ArchivePathResolver>());

    [Fact]
    public async Task ReadingManyMembersOfOneBigArchiveAllocatesLittlePerMember()
    {
        // Each member used to cost a parse of the archive's whole index and a store archive of its own, a few tens
        // of MB of short-lived arrays in all; a list draws tens of thousands of members out of archives.
        const int Members = 300;
        var loadout = await CreateLoadout();
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("data/pack.bsa")] = BigBsa });
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64Identity()!.Value;
        using var resolver = NewResolver(new ArchivePlan([]), loadout);
        var via = new[] { RelativePath.FromUnsanitizedInput("data/pack.bsa") };
        await resolver.Resolve(new ArchiveHashPath(hash, [.. via, RelativePath.FromUnsanitizedInput(NameOf(ArchiveMembers - 1))]), CancellationToken.None);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var gate = new SemaphoreSlim(8);
        await Task.WhenAll(Enumerable.Range(0, Members).Select(async i =>
        {
            await gate.WaitAsync();
            try { await resolver.Resolve(new ArchiveHashPath(hash, [.. via, RelativePath.FromUnsanitizedInput(NameOf(i))]), CancellationToken.None); }
            finally { gate.Release(); }
        }));
        var perMember = (GC.GetTotalAllocatedBytes(precise: true) - before) / Members;

        helper.WriteLine($"allocated per member: {perMember / 1024} KB");
        perMember.Should().BeLessThan(2 * 1024 * 1024);
    }

    [Fact]
    [Trait("Benchmark", "True")]
    public async Task MembersOfAGameFolderArchiveReportRetainedMemory()
    {
        var count = Env("SILVER_BSA_RESOLVES", 3000);
        var loadout = await CreateLoadout();
        var gameHash = Hash64(BigBsa);
        var state = new GameFileSourceState("SkyrimSpecialEdition", GameBsa, gameHash, GameVersion);
        var plan = new ArchivePlan([new ArchivePlanRow(Entry(state, gameHash, BigBsa.Length), SilverModManager.Wabbajack.Acquire.ArchiveState.GameFile, null, null, null)]);
        using var resolver = NewResolver(plan, loadout);

        var (retained, peak, peakWs) = await ResolveMembers(resolver, gameHash, count, []);

        var line = $"BSAMEM kind=game resolves={count} archiveMB={BigBsa.Length / 1048576.0:F1} retainedMB={retained / 1048576.0:F1} retainedPerMemberKB={retained / 1024.0 / count:F1} peakAboveStartMB={peak / 1048576.0:F0} peakWorkingSetMB={peakWs / 1048576}";
        helper.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
    }

    [Fact]
    [Trait("Benchmark", "True")]
    public async Task MembersOfAnArchiveInTheFileStoreReportRetainedMemory()
    {
        var count = Env("SILVER_BSA_RESOLVES", 3000);
        var loadout = await CreateLoadout();
        await using var zip = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [RelativePath.FromUnsanitizedInput("data/pack.bsa")] = BigBsa });
        var library = await RegisterLocalArchive(zip.Path);
        var hash = library.AsLibraryFile().XxHash64Identity()!.Value;
        using var resolver = NewResolver(new ArchivePlan([]), loadout);

        var (retained, peak, peakWs) = await ResolveMembers(resolver, hash, count, [RelativePath.FromUnsanitizedInput("data/pack.bsa")]);

        var line = $"BSAMEM kind=store resolves={count} archiveMB={BigBsa.Length / 1048576.0:F1} retainedMB={retained / 1048576.0:F1} retainedPerMemberKB={retained / 1024.0 / count:F1} peakAboveStartMB={peak / 1048576.0:F0} peakWorkingSetMB={peakWs / 1048576}";
        helper.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
    }

    [Fact]
    [Trait("Benchmark", "True")]
    public async Task TheOpenerAloneAllocatesPerMember()
    {
        var count = 300;
        var opener = new BsaMemberOpener();
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < count; i++)
        {
            await using var stream = (await opener.Open(new MemoryStream(BigBsa), RelativePath.FromUnsanitizedInput(NameOf(i)), CancellationToken.None))!;
        }
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        var line = $"BSAMEM kind=opener-alone allocatedPerMemberKB={allocated / 1024.0 / count:F0}";
        helper.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
    }

    [Fact]
    [Trait("Benchmark", "True")]
    public async Task TheStoreAloneAllocatesPerBackup()
    {
        var count = Env("SILVER_BSA_RESOLVES", 3000);
        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < count; i++)
        {
            var bytes = MemberData(i);
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(RelativePath.FromUnsanitizedInput("x"), new MemoryStream(bytes)),
                FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(bytes.Length))], deduplicate: true);
        }
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        var line = $"BSAMEM kind=store-alone backups={count} allocatedPerBackupKB={allocated / 1024.0 / count:F0}";
        helper.WriteLine(line);
        if (Environment.GetEnvironmentVariable("SILVER_BENCH_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + "\n");
    }
}
