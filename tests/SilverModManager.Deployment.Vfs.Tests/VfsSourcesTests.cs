using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Deployment.Vfs.Testing;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs.Tests;

/// <summary>
/// Drives the real synchronizer over a stubbed game and checks that the tree serves exactly what it
/// picked, including a winning delete over a vanilla file.
/// </summary>
public class VfsSourcesTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<VfsSourcesTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    // Created on demand: the tree only opens files that exist.
    private readonly AbsolutePath _staging = NexusMods.Paths.FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-sources-tests-{Guid.NewGuid():N}");

    private AbsolutePath StagedPathFor(Hash hash) => _staging.Combine(hash.ToString());

    protected override async Task GenerateGameFiles()
    {
        foreach (var (path, content) in new[] { ("Data/image.dds", "vanilla image"), ("README.txt", "vanilla readme") })
        {
            var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, path));
            file.Parent.CreateDirectory();
            await file.WriteAllTextAsync(content);
        }
    }

    [Fact]
    public async Task TheTreeServesTheHashesTheSynchronizerPicked()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var low = AddEmptyGroup(tx, loadout.LoadoutId, "Low");
        var high = AddEmptyGroup(tx, loadout.LoadoutId, "High");
        AddFile(tx, loadout.LoadoutId, low, new GamePath(LocationId.Game, "Data/conflict.txt"), "from low");
        AddFile(tx, loadout.LoadoutId, low, new GamePath(LocationId.Game, "Data/only-in-low.txt"), "only low", out var onlyLowHash, out _);
        AddFile(tx, loadout.LoadoutId, high, new GamePath(LocationId.Game, "Data/conflict.txt"), "from high", out var fromHighHash, out _);
        AddDelete(tx, loadout.LoadoutId, high, new GamePath(LocationId.Game, "Data/image.dds"));
        // "Low" starts on top; the manager has to move "High" above it, the way the conflict UI does.
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = low, Priority = ConflictPriority.From(2) };
        var highPriority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = high, Priority = ConflictPriority.From(1) };
        var result = await tx.Commit();

        await LoadoutManager.WinAllFileConflicts([LoadoutItemGroupPriorityId.From(result[highPriority.Id].Value)]);
        loadout = loadout.Rebase();

        var tree = await Synchronizer.BuildSyncTree(loadout);
        var deleted = ((ALoadoutSynchronizer)Synchronizer).DeletedWinners(loadout);

        var set = VfsSources.FromSyncTree(tree, deleted, DataRoot, StagedPathFor);

        var picked = tree[new GamePath(LocationId.Game, "Data/conflict.txt")].Loadout.Hash;
        picked.Should().Be(fromHighHash, "winning all conflicts for High puts its copy of the file on top");
        tree[new GamePath(LocationId.Game, "Data/only-in-low.txt")].Loadout.Hash.Should().Be(onlyLowHash);
        set.Unserved.Should().BeEmpty();
        set.Sources.Select(s => s.Path.ToString()).Should().BeEquivalentTo("conflict.txt", "only-in-low.txt", "image.dds");
        set.Sources.Should().OnlyContain(s => !s.Path.ToString().StartsWith("Data/", StringComparison.OrdinalIgnoreCase));

        var conflict = set.Sources.Single(s => s.Path == RelativePath.FromUnsanitizedInput("conflict.txt"));
        conflict.Kind.Should().Be(VfsSourceKind.Staged);
        conflict.Hash.Should().Be(picked);
        set.Sources.Single(s => s.Path == RelativePath.FromUnsanitizedInput("image.dds")).Kind.Should().Be(VfsSourceKind.Hidden);

        _staging.CreateDirectory();
        try
        {
            await StagedPathFor(picked).WriteAllTextAsync("staged bytes");

            var kernel = new FakeFuseKernel();
            await kernel.Mount(VfsTree.Build(_staging, [], set.Sources, VfsCachePolicy.Default), FuseMountOptions.Default(_staging), CancellationToken.None);

            var served = kernel.Open("conflict.txt", VfsOpenFlags.Read);
            served.Ok.Should().BeTrue();
            served.Value!.RealPath.Should().Be(StagedPathFor(picked));
            kernel.Release(served.Value).Ok.Should().BeTrue();
            kernel.Resolve("only-in-low.txt").Ok.Should().BeTrue();
            kernel.Resolve("image.dds").Error.Should().Be(Errno.ENOENT);
        }
        finally
        {
            _staging.DeleteDirectory(recursive: true);
        }
    }

    // Same shape the copy backend writes when the game deletes a file it was serving.
    private static void AddDelete(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path)
    {
        _ = new DeletedFile.New(tx, out var id)
        {
            Reason = "Reified delete",
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = path.ToGamePathParentTuple(loadoutId),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    Name = path.Path,
                    ParentId = group,
                },
            },
        };
    }
}
