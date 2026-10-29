using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.App.GarbageCollection.DataModel.Tests.FindUsedFiles;

/// <summary>
/// The models whose xxHash3 store keys the collector roots hand those keys out as <see cref="FileIdentity"/>.
/// </summary>
public class StoreKeyIdentityTests(ITestOutputHelper testOutputHelper) : AGCStubbedGameTest<StoreKeyIdentityTests>(testOutputHelper)
{
    private static readonly Hash Key = Hash.From(0x0123456789ABCDEF);
    private static readonly Hash Other = Hash.From(0x1111222233334444);

    [Fact]
    public async Task LoadoutFilesExposeTheirHashAsAnIdentity()
    {
        await CreateLoadout();

        var files = LoadoutFile.All(Connection.Db).ToArray();

        files.Should().NotBeEmpty();
        foreach (var file in files)
        {
            file.Identity.Equals(FileIdentity.XxHash3(file.Hash)).Should().BeTrue();
            ((IHavePathHashSizeAndReference)file).Identity.Equals(file.Identity).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GameBackedUpFilesExposeTheirHashAsAnIdentity()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new GameBackedUpFile.New(tx) { Hash = Key, GameInstallId = loadout.InstallationId };
            await tx.Commit();
        }

        var file = GameBackedUpFile.All(Connection.Db).Single(f => f.Hash == Key);

        file.Identity.Equals(FileIdentity.XxHash3(Key)).Should().BeTrue();
    }

    [Fact]
    public async Task LoadoutIniStatesExposeBaselineAndLastBakeAsIdentities()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = new GamePath(LocationId.Preferences, "Game.ini").ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = Key,
            };
            _ = new LoadoutIniState.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                Path = new GamePath(LocationId.Preferences, "Other.ini").ToGamePathParentTuple(loadout.LoadoutId),
                Baseline = Other,
                LastBakedHash = Key,
            };
            await tx.Commit();
        }

        var states = LoadoutIniState.All(Connection.Db).ToArray();
        var neverBaked = states.Single(s => s.Baseline == Key);
        var baked = states.Single(s => s.Baseline == Other);

        neverBaked.BaselineIdentity.Equals(FileIdentity.XxHash3(Key)).Should().BeTrue();
        neverBaked.LastBakedIdentity().Should().BeNull("nothing was written yet");
        baked.LastBakedIdentity().Should().NotBeNull();
        baked.LastBakedIdentity()!.Value.Equals(FileIdentity.XxHash3(Key)).Should().BeTrue();
    }

    [Fact]
    public async Task LibraryFilesExposeTheirHashesAsIdentities()
    {
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LibraryFile.New(tx, out var withBoth)
            {
                FileName = "Both.esp",
                Hash = Key,
                XxHash64 = Other,
                Size = Size.From(1),
                LibraryItem = new LibraryItem.New(tx, withBoth) { Name = "Both.esp" },
            };
            _ = new LibraryFile.New(tx, out var plain)
            {
                FileName = "Plain.esp",
                Hash = Other,
                Size = Size.From(1),
                LibraryItem = new LibraryItem.New(tx, plain) { Name = "Plain.esp" },
            };
            await tx.Commit();
        }

        var both = LibraryFile.All(Connection.Db).Single(f => f.Hash == Key);
        var plainFile = LibraryFile.All(Connection.Db).Single(f => f.Hash == Other);

        both.Identity.Equals(FileIdentity.XxHash3(Key)).Should().BeTrue();
        both.XxHash64Identity()!.Value.Equals(FileIdentity.XxHash64(Other)).Should().BeTrue();
        both.XxHash64Identity()!.Value.Equals(FileIdentity.XxHash3(Other)).Should().BeFalse("the kinds are tagged apart");
        plainFile.XxHash64Identity().Should().BeNull("rows from before it was recorded lack it");
    }
}
