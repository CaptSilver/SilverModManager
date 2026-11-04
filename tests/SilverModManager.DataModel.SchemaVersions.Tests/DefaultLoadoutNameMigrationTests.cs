using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Loadouts used to be named "Loadout A", "Loadout B" and so on. They are named after their game now, and the
/// migration renames the ones whose name is still the automatic one.
/// </summary>
public class DefaultLoadoutNameMigrationTests
{
    [Fact]
    public async Task MigrateAll_AutomaticNamesOfOneGame_BecomeTheGameNameAndNumberedNames()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var (a, b) = await SeedTwo(connection, "Loadout A", "Loadout B");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, a).Should().Be("Stubbed Game");
        NameOf(connection, b).Should().Be("Stubbed Game 2");
        Loadout.Load(connection.Db, a).ShortName.Should().Be("A", "the badge is not part of the rename");
    }

    [Fact]
    public async Task MigrateAll_SuffixedAutomaticName_IsRenamed()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var (a, b) = await SeedTwo(connection, "Loadout A", "Loadout AB (2)");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, a).Should().Be("Stubbed Game");
        NameOf(connection, b).Should().Be("Stubbed Game 2");
    }

    [Fact]
    public async Task MigrateAll_UserChosenName_IsLeftAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var (a, mine) = await SeedTwo(connection, "Loadout A", "Hardcore run");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, a).Should().Be("Stubbed Game");
        NameOf(connection, mine).Should().Be("Hardcore run");
    }

    [Fact]
    public async Task MigrateAll_UserLoadoutAlreadyNamedAfterTheGame_IsNotDuplicated()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var (a, mine) = await SeedTwo(connection, "Loadout A", "stubbed game");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, a).Should().Be("Stubbed Game 2");
        NameOf(connection, mine).Should().Be("stubbed game");
    }

    [Fact]
    public async Task MigrateAll_TwoGames_AreNumberedIndependently()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId one, two, three, four;
        using (var tx = connection.BeginTransaction())
        {
            var first = NewInstall(tx, "Stubbed Game", "/games/one");
            var second = NewInstall(tx, "Other Game", "/games/two");
            var l1 = NewLoadout(tx, first, "Loadout A", "A");
            var l2 = NewLoadout(tx, second, "Loadout A", "A");
            var l3 = NewLoadout(tx, first, "Loadout B", "B");
            var l4 = NewLoadout(tx, second, "Loadout B", "B");
            var result = await tx.Commit();
            (one, two, three, four) = (result.Remap(l1).Id, result.Remap(l2).Id, result.Remap(l3).Id, result.Remap(l4).Id);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, one).Should().Be("Stubbed Game");
        NameOf(connection, three).Should().Be("Stubbed Game 2");
        NameOf(connection, two).Should().Be("Other Game");
        NameOf(connection, four).Should().Be("Other Game 2");
    }

    [Fact]
    public async Task MigrateAll_RunTwice_SecondRunWritesNothing()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        await SeedTwo(connection, "Loadout A", "Loadout B");
        var migrations = provider.GetRequiredService<MigrationService>();

        await migrations.MigrateAll();
        var names = Loadout.All(connection.Db).Select(l => l.Name).Order().ToArray();

        // MigrateAll skips a migration it has already recorded, so run this one again by hand.
        using (var tx = connection.BeginTransaction())
        {
            new _0009_NameLoadoutsAfterTheirGame(NullLogger<_0009_NameLoadoutsAfterTheirGame>.Instance).Migrate(tx, connection.Db);
            await tx.Commit();
        }

        Loadout.All(connection.Db).Select(l => l.Name).Order().Should().Equal(names);
    }

    [Fact]
    public async Task MigrateAll_LeftoverDeletedLoadout_IsNotRenamedAndDoesNotTakeTheGameName()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId leftover, visible;
        using (var tx = connection.BeginTransaction())
        {
            var install = NewInstall(tx, "Stubbed Game", "/games/stubbed");
            var l1 = NewLoadout(tx, install, "Loadout A", "A");
            l1.LoadoutKind = LoadoutKind.Deleted;
            var l2 = NewLoadout(tx, install, "Loadout B", "B");
            var result = await tx.Commit();
            (leftover, visible) = (result.Remap(l1).Id, result.Remap(l2).Id);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        NameOf(connection, visible).Should().Be("Stubbed Game");
        NameOf(connection, leftover).Should().Be("Loadout A");
    }

    private static string NameOf(IConnection connection, EntityId loadout) => Loadout.Load(connection.Db, loadout).Name;

    private static async Task<(EntityId First, EntityId Second)> SeedTwo(IConnection connection, string firstName, string secondName)
    {
        using var tx = connection.BeginTransaction();
        var install = NewInstall(tx, "Stubbed Game", "/games/stubbed");
        var first = NewLoadout(tx, install, firstName, "A");
        var second = NewLoadout(tx, install, secondName, "B");
        var result = await tx.Commit();
        return (result.Remap(first).Id, result.Remap(second).Id);
    }

    private static GameInstallMetadata.New NewInstall(ITransaction tx, string gameName, string path) => new(tx)
    {
        Store = GameStore.Steam,
        Path = path,
        Game = GameId.From(gameName.Replace(" ", "")),
        Name = gameName,
    };

    private static Loadout.New NewLoadout(ITransaction tx, GameInstallMetadata.New installation, string name, string shortName) => new(tx)
    {
        Name = name,
        ShortName = shortName,
        InstallationId = installation,
        LoadoutKind = LoadoutKind.Default,
        Revision = 0,
        GameVersion = VanityVersion.From("Unknown"),
    };
}
