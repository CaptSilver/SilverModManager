using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.DataModel.Tests;

public class LoadoutNameUniquenessTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LoadoutNameUniquenessTests>(helper)
{
    [Fact]
    public async Task CreateLoadout_AppendsSuffixWhenNameIsTaken_IgnoringCase()
    {
        var first = await LoadoutManager.CreateLoadout(GameInstallation, "Create Dupe");
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "create dupe");
        var third = await LoadoutManager.CreateLoadout(GameInstallation, "  CREATE DUPE ");

        first.Name.Should().Be("Create Dupe");
        second.Name.Should().Be("create dupe (2)");
        third.Name.Should().Be("CREATE DUPE (3)");
    }

    [Fact]
    public async Task CreateLoadout_AllowsSameNameOnAnotherInstallation()
    {
        var otherInstallation = await SeedLoadoutOnAnotherInstallation("Cross Install");

        var created = await LoadoutManager.CreateLoadout(GameInstallation, "Cross Install");

        created.Name.Should().Be("Cross Install");
        created.InstallationId.Should().NotBe(otherInstallation.InstallationId);
    }

    [Fact]
    public async Task CreateLoadout_IgnoresDeletedLoadouts()
    {
        var first = await LoadoutManager.CreateLoadout(GameInstallation, "Deleted Twin");
        await LoadoutManager.DeleteLoadout(first.LoadoutId);

        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Deleted Twin");

        second.Name.Should().Be("Deleted Twin");
    }

    [Fact]
    public async Task CopyLoadout_NamesCopyAfterItsShortName()
    {
        var original = await LoadoutManager.CreateLoadout(GameInstallation, "Copy Source");

        var copy = await LoadoutManager.CopyLoadout(original.LoadoutId);

        copy.Name.Should().Be("Loadout " + copy.ShortName);
    }

    [Fact]
    public async Task CopyLoadout_AppendsSuffixWhenDefaultNameIsTaken()
    {
        var original = await LoadoutManager.CreateLoadout(GameInstallation, "Copy Taken Source");
        var probe = await LoadoutManager.CopyLoadout(original.LoadoutId);
        var shortName = probe.ShortName;
        await LoadoutManager.DeleteLoadout(probe.LoadoutId);

        // The next copy gets the freed short name, so take the name that copy would use.
        await LoadoutManager.RenameLoadout(original.LoadoutId, "Loadout " + shortName);
        var copy = await LoadoutManager.CopyLoadout(original.LoadoutId);

        copy.ShortName.Should().Be(shortName);
        copy.Name.Should().Be($"Loadout {shortName} (2)");
    }

    [Fact]
    public async Task RenameLoadout_RenamesToFreeName()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Rename Before");

        var renamed = await LoadoutManager.RenameLoadout(loadout.LoadoutId, "  Rename After ");

        renamed.Name.Should().Be("Rename After");
        Loadout.Load(Connection.Db, loadout.LoadoutId).Name.Should().Be("Rename After");
    }

    [Fact]
    public async Task RenameLoadout_AppendsSuffixWhenNameBelongsToAnotherLoadout()
    {
        await LoadoutManager.CreateLoadout(GameInstallation, "Rename Taken");
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Rename Mover");

        var renamed = await LoadoutManager.RenameLoadout(loadout.LoadoutId, "RENAME TAKEN");

        renamed.Name.Should().Be("RENAME TAKEN (2)");
    }

    [Fact]
    public async Task RenameLoadout_ToOwnNameIsANoOp()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Rename Self");
        var revisionBefore = Connection.Db.BasisTxId;

        var renamed = await LoadoutManager.RenameLoadout(loadout.LoadoutId, "Rename Self");

        renamed.Name.Should().Be("Rename Self");
        Connection.Db.BasisTxId.Should().Be(revisionBefore);
    }

    [Fact]
    public async Task RenameLoadout_RejectsBlankNames()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "Rename Blank");

        var act = async () => await LoadoutManager.RenameLoadout(loadout.LoadoutId, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenameLoadout_RejectsAnIdThatIsNotALoadout()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);

        var act = async () => await LoadoutManager.RenameLoadout(LoadoutId.From(metadata.Id), "Not A Loadout");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*not a loadout*");
    }

    /// <summary>
    /// Writes a second installation of the same game, from another store, straight into the DB. The registry tells
    /// installations apart by game and store only, so the store must differ and the tested installation must be
    /// managed first, or creating a loadout for it would adopt this row.
    /// </summary>
    private async Task<Loadout.ReadOnly> SeedLoadoutOnAnotherInstallation(string name)
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var otherStore = GameInstallation.LocatorResult.Store == GameStore.GOG ? GameStore.Steam : GameStore.GOG;

        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = $"/tmp/another-installation-{Guid.NewGuid()}",
            Name = GameInstallation.Game.DisplayName,
            Store = otherStore,
            Game = GameInstallation.Game.GameId,
            GameId = GameInstallation.Game.NexusModsGameId.Value,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }
}
