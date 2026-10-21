using System.Collections.Frozen;
using System.Reflection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.Games.FileHashes.Tests;

/// <summary>
/// The notice about non-vanilla game files, read from baseline rows written into an in-memory local store.
/// </summary>
public sealed class NonVanillaGameFilesEmitterTests : IDisposable
{
    private const string GamePath = "/games/test";

    private readonly RealQueryEngineHost _host = new();
    private readonly IConnection _main;
    private readonly IConnection _local;
    private readonly NonVanillaGameFilesEmitter _emitter;

    public NonVanillaGameFilesEmitterTests()
    {
        _main = _host.OpenInMemory("main");
        _local = _host.OpenInMemory("local_hashes");
        var hashes = Substitute.For<IFileHashesService>();
        hashes.Local.Returns(_ => _local.Db);
        _emitter = new NonVanillaGameFilesEmitter(hashes);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task NoBaseline_YieldsNothing()
    {
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineWithoutReports_YieldsNothing()
    {
        await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineOfAnotherInstallation_YieldsNothing()
    {
        await HashRows.AddLocalBaseline(_local, GameStore.Steam, "/games/other", LocalBaselineKind.DepotManifests, [], "mod.esp");
        await HashRows.AddLocalBaseline(_local, GameStore.GOG, GamePath, LocalBaselineKind.DepotManifests, [], "mod.esp");
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        (await Diagnose(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineWithReports_YieldsOneDiagnosticListingEachPathAndReason()
    {
        var baseline = await HashRows.AddLocalBaseline(_local, GameStore.Steam, GamePath, LocalBaselineKind.DepotManifests, [("a.esm", "vanilla")]);
        await AddReports(baseline, ("mod.esp", NonVanillaReason.NotInManifest), ("data/other.pak", NonVanillaReason.SizeDiffers), ("b.esm", NonVanillaReason.HashDiffers));
        var loadout = await LoadoutFor(GameStore.Steam, GamePath);

        var diagnostic = (await Diagnose(loadout)).Should().ContainSingle().Subject;

        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.NonVanillaGameFilesMessageData>>().Subject.MessageData;
        data.FileCount.Should().Be(3);
        data.Store.Should().Be(GameStore.Steam.Value);
        data.Game.Should().Be("Test game");
        data.Files.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Should().Equal(
            "* `b.esm` (content differs)",
            "* `data/other.pak` (size differs)",
            "* `mod.esp` (not in the store's manifest)"
        );
    }

    [Fact]
    public void DiagnosticIds_AreUniqueWithinTheSource()
    {
        // The template fields throw if read at runtime (they only feed the source generator), so the ids are
        // read from the Create methods the generator emits for them.
        var ids = typeof(Diagnostics)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Create", StringComparison.Ordinal) && typeof(Diagnostic).IsAssignableFrom(m.ReturnType))
            .Select(m => ((Diagnostic)m.Invoke(null, m.GetParameters().Select(DefaultArgument).ToArray())!).Id)
            .ToArray();

        ids.Should().HaveCountGreaterThan(2);
        ids.Should().OnlyHaveUniqueItems();
    }

    private static object DefaultArgument(ParameterInfo parameter)
        => parameter.ParameterType == typeof(string) ? ""
            : parameter.ParameterType == typeof(Size) ? Size.From(0)
            : Activator.CreateInstance(parameter.ParameterType)!;

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly loadout)
    {
        var results = new List<Diagnostic>();
        await foreach (var diagnostic in _emitter.Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            results.Add(diagnostic);
        return results.ToArray();
    }

    private async Task AddReports(EntityId baseline, params (string Path, NonVanillaReason Reason)[] reports)
    {
        using var tx = _local.BeginTransaction();
        foreach (var (path, reason) in reports)
        {
            _ = new NonVanillaFile.New(tx)
            {
                BaselineId = baseline,
                Path = RelativePath.FromUnsanitizedInput(path),
                Reason = reason,
                Size = Size.From(1),
            };
        }
        await tx.Commit();
    }

    private async Task<Loadout.ReadOnly> LoadoutFor(GameStore store, string gamePath)
    {
        var id = await HashRows.AddLoadout(_main, store, gamePath);
        return Loadout.Load(_main.Db, id);
    }
}
