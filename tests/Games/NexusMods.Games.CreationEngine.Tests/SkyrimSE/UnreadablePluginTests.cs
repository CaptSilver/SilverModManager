using System.Collections.Frozen;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Size = NexusMods.Paths.Size;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.Emitters;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>
/// A plugin whose bytes no stream source can supply must come out as "unreadable", never as a null stream
/// handed to the plugin parser.
/// </summary>
public class UnreadablePluginTests(ITestOutputHelper helper) : AIsolatedGameTest<UnreadablePluginTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private static readonly DiagnosticId MissingMasterId = new("NexusMods.Games.CreationEngine", number: 1);
    private static readonly DiagnosticId UnreadablePluginsId = new("NexusMods.Games.CreationEngine", number: 3);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);
    }

    private ICreationEngineGame Creation => Game;

    // Created before any plugin is stored: creating a loadout runs the garbage collector, which would
    // collect stored files nothing references yet.
    private Loadout.ReadOnly? _loadout;
    private async Task<Loadout.ReadOnly> EnsureLoadout() => _loadout ??= await CreateLoadout();

    private static GamePath DataPath(string fileName) => new(LocationId.Game, "Data/" + fileName);

    private static Task<byte[]> PluginBytes(string fileName, params string[] masters)
        => Task.FromResult(SkyrimPluginFile.Bytes(fileName, masters));

    /// <summary>A loadout plugin whose bytes the file store has.</summary>
    private async Task<(GamePath Path, SyncNode Node)> StoredPlugin(string fileName, params string[] masters)
    {
        await EnsureLoadout();
        var bytes = await PluginBytes(fileName, masters);
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(fileName, new MemoryStream(bytes)), hash, size)]);
        return (DataPath(fileName), LoadoutNode(hash, size));
    }

    /// <summary>A loadout plugin that no store holds, like a game file whose recorded build is no longer installed.</summary>
    private static (GamePath Path, SyncNode Node) MissingPlugin(string fileName, ulong hashValue)
    {
        return (DataPath(fileName), LoadoutNode(Hash.From(hashValue), Size.FromLong(4096)));
    }

    private static SyncNode LoadoutNode(Hash hash, Size size) => new()
    {
        Loadout = new SyncNodePart { Hash = hash, Size = size, LastModifiedTicks = 0 },
        SourceItemType = LoadoutSourceItemType.Game,
    };

    /// <summary>Puts a plugin in the game folder and has the index record it, like a file the game store updated.</summary>
    private async Task<SyncNodePart> PluginOnDisk(string fileName, params string[] masters)
    {
        await EnsureLoadout();
        var bytes = await PluginBytes(fileName, masters);
        var file = GameInstallation.Locations.ToAbsolutePath(DataPath(fileName));
        file.Parent.CreateDirectory();
        await file.WriteAllBytesAsync(bytes);
        await Synchronizer.ReindexState(GameInstallation);
        return new SyncNodePart { Hash = bytes.xxHash3(), Size = Size.FromLong(bytes.Length), LastModifiedTicks = 0 };
    }

    private static FrozenDictionary<GamePath, SyncNode> Tree(params (GamePath Path, SyncNode Node)[] nodes)
        => nodes.ToDictionary(n => n.Path, n => n.Node).ToFrozenDictionary();

    private async Task<List<Diagnostic>> Diagnose(FrozenDictionary<GamePath, SyncNode> tree)
    {
        var loadout = await EnsureLoadout();
        var emitter = new MissingMasterEmitter(Creation);
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, tree, CancellationToken.None))
            result.Add(diagnostic);
        return result;
    }

    private string Summary(Diagnostic diagnostic) => diagnostic.FormatSummary(new PlainWriter());

    [Fact]
    public async Task ParsePlugin_NoStoreHasTheBytes_ReturnsNull()
    {
        var header = await Creation.ParsePlugin(Hash.From(0xDEAD), RelativePath.FromUnsanitizedInput("Gone.esp"));

        header.Should().BeNull();
    }

    [Fact]
    public async Task ParsePlugin_StoredPlugin_ReturnsItsHeader()
    {
        var (_, node) = await StoredPlugin("Readable.esp", "Skyrim.esm");

        var header = await Creation.ParsePlugin(node.Loadout.Hash, RelativePath.FromUnsanitizedInput("Readable.esp"));

        header.Should().NotBeNull();
        header!.Masters.Should().Equal("Skyrim.esm");
    }

    [Fact]
    public async Task MissingMasterEmitter_NamesThePluginsItCouldNotRead()
    {
        var readable = await StoredPlugin("Readable.esp", "Absent.esm");
        var diagnostics = await Diagnose(Tree(readable, MissingPlugin("GoneA.esp", 0xA1), MissingPlugin("GoneB.esl", 0xB2)));

        // The readable plugin is still checked for its masters.
        diagnostics.Should().Contain(d => d.Id == MissingMasterId);

        var unreadable = diagnostics.Should().ContainSingle(d => d.Id == UnreadablePluginsId).Subject;
        var summary = Summary(unreadable);
        summary.Should().Contain("GoneA.esp").And.Contain("GoneB.esl").And.NotContain("Readable.esp");
    }

    [Fact]
    public async Task MissingMasterEmitter_DoesNotReportAnUnreadableMasterAsMissing()
    {
        // The master is in the game folder, only its header can't be read, so its dependants are not missing it.
        var dependant = await StoredPlugin("Dependant.esp", "GoneMaster.esm");

        var diagnostics = await Diagnose(Tree(dependant, MissingPlugin("GoneMaster.esm", 0xD4)));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be(UnreadablePluginsId);
    }

    [Fact]
    public async Task MissingMasterEmitter_EmitsNothingExtraWhenEveryPluginIsReadable()
    {
        var readable = await StoredPlugin("Readable.esp", "Skyrim.esm");
        var master = await StoredPlugin("Skyrim.esm");

        var diagnostics = await Diagnose(Tree(readable, master));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingMasterEmitter_ReadsTheFileOnDiskWhenTheRecordedBytesAreGone()
    {
        // The recorded hash is of a build that is no longer installed; the file the store put there is readable.
        var disk = await PluginOnDisk("Patched.esp", "Absent.esm");
        var node = LoadoutNode(Hash.From(0xC3), Size.FromLong(4096)) with { Disk = disk };

        var diagnostics = await Diagnose(Tree((DataPath("Patched.esp"), node)));

        diagnostics.Should().NotContain(d => d.Id == UnreadablePluginsId);
        diagnostics.Should().ContainSingle(d => d.Id == MissingMasterId);
    }

    [Fact]
    public async Task MissingMasterEmitter_MatchesAMasterNamedInADifferentCase()
    {
        // The game's file lookup ignores case, so a master reference spelled differently is still present.
        var readable = await StoredPlugin("Readable.esp", "skyrim.esm");
        var master = await StoredPlugin("Skyrim.esm");

        var diagnostics = await Diagnose(Tree(readable, master));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingMasterEmitter_WithoutASyncTree_ReportsNothing()
    {
        var loadout = await EnsureLoadout();
        var emitter = new MissingMasterEmitter(Creation);

        var diagnostics = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None))
            diagnostics.Add(diagnostic);

        diagnostics.Should().BeEmpty();
    }

    private PluginsFile NewPluginsFile() => new(NullLogger<PluginsFile>.Instance, Creation, ServiceProvider.GetRequiredService<ISorter>());

    [Fact]
    public async Task PluginsFile_SortsAPluginAfterAMasterNamedInADifferentCase()
    {
        var dependant = await StoredPlugin("A.esp", "z.esp");
        var master = await StoredPlugin("Z.esp");
        var loadout = await EnsureLoadout();

        using var stream = new MemoryStream();
        await NewPluginsFile().Write(stream, loadout, Tree(dependant, master).ToDictionary());

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Where(l => !l.StartsWith('#')).Should().Equal("*Z.esp", "*A.esp");
    }

    [Fact]
    public async Task PluginsFile_RefusesToWriteWhenAPluginCannotBeRead()
    {
        var readable = await StoredPlugin("Readable.esp");
        var loadout = await EnsureLoadout();
        var tree = Tree(readable, MissingPlugin("GoneA.esp", 0xA1), MissingPlugin("GoneB.esl", 0xB2)).ToDictionary();

        using var stream = new MemoryStream();
        var act = async () => await NewPluginsFile().Write(stream, loadout, tree);

        var failure = await act.Should().ThrowAsync<UnreadablePluginsException>();
        failure.Which.Message.Should().Contain("GoneA.esp").And.Contain("GoneB.esl").And.NotContain("Readable.esp");
        stream.Length.Should().Be(0, "a plugin missing from Plugins.txt is a disabled plugin, so nothing may be written");
    }

    [Fact]
    public async Task PluginsFile_ListsAPluginReadFromDiskWhenTheRecordedBytesAreGone()
    {
        var disk = await PluginOnDisk("Patched.esp");
        var loadout = await EnsureLoadout();
        var node = LoadoutNode(Hash.From(0xC3), Size.FromLong(4096)) with { Disk = disk };
        var tree = Tree((DataPath("Patched.esp"), node)).ToDictionary();

        using var stream = new MemoryStream();
        await NewPluginsFile().Write(stream, loadout, tree);

        Encoding.UTF8.GetString(stream.ToArray()).Should().Contain("*Patched.esp");
    }

    [Fact]
    public async Task WritingIntrinsics_PluginsFileRefuses_KeepsThePreviousPluginsTxt()
    {
        var readable = await StoredPlugin("Readable.esp");
        var loadout = await EnsureLoadout();
        var pluginsTxt = GameInstallation.Locations.ToAbsolutePath(Creation.PluginsFile);
        pluginsTxt.Parent.CreateDirectory();
        const string previous = "*Readable.esp\n*GoneA.esp\n";
        await pluginsTxt.WriteAllTextAsync(previous);

        var tree = Tree(readable, MissingPlugin("GoneA.esp", 0xA1)).ToDictionary();
        tree[Creation.PluginsFile] = new SyncNode { SourceItemType = LoadoutSourceItemType.Intrinsic, Actions = Actions.WriteIntrinsic };

        var backend = ServiceProvider.GetServices<IDeploymentBackend>().Single(b => b.Id == DeploymentBackendId.Copy);
        using var tx = Connection.BeginTransaction();
        var context = new DeploymentContext
        {
            Synchronizer = (ALoadoutSynchronizer)Synchronizer,
            Installation = GameInstallation,
            MetadataId = loadout.InstallationId,
            SyncTree = tree,
            Loadout = loadout,
            Transaction = tx,
        };

        var act = async () => await backend.WriteIntrinsics(context, tree);

        await act.Should().ThrowAsync<UnreadablePluginsException>();
        (await pluginsTxt.ReadAllTextAsync()).Should().Be(previous, "an empty Plugins.txt disables every plugin");
    }

    private sealed class PlainWriter : IDiagnosticWriter
    {
        public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull => Write(ref state, value.ToString().AsSpan());

        public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct => Write(ref state, value.ToString().AsSpan());

        public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value) => state.StringBuilder.Append(value);
    }
}
