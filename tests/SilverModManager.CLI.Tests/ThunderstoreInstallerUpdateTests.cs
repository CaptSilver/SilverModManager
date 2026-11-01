using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Moving a package a loadout links to its newest version, against a package index and package downloads answered
/// from a table. The library holds REPOLib 4.1.0; the index has 4.2.0, which also needs BepInExPack.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreInstallerUpdateTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreInstallerUpdateTests, ThunderstoreStubbedGame>(helper)
{
    private static readonly ThunderstorePackageRef RepoLibPackage = new("Zehs", "REPOLib");
    private static readonly ThunderstoreVersionRef Newest = new(RepoLibPackage, new ThunderstoreVersionNumber(4, 2, 0));
    private static readonly ThunderstoreVersionRef Loader = new(new ThunderstorePackageRef("BepInEx", "BepInExPack"), new ThunderstoreVersionNumber(5, 4, 2305));

    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        return services;
    }

    private IThunderstoreInstaller Installer => ServiceProvider.GetRequiredService<IThunderstoreInstaller>();

    // The names of the library items a loadout links, one entry per link.
    private string[] Linked(Loadout.ReadOnly loadout)
        => LibraryLinkedLoadoutItem.All(Connection.Db)
            .Where(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId)
            .Select(link => link.LibraryItem.Name)
            .Order()
            .ToArray();

    private bool IsEnabled(Loadout.ReadOnly loadout, string itemName)
        => LibraryLinkedLoadoutItem.All(Connection.Db)
            .Single(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId && link.LibraryItem.Name == itemName)
            .AsLoadoutItem().IsEnabled();

    private bool InLibrary(string fileName) => LibraryFile.All(Connection.Db).Any(file => file.FileName == fileName);

    private async Task<(Loadout.ReadOnly Loadout, LibraryItem.ReadOnly Old)> LoadoutLinking410(bool enabled = true)
    {
        var loadout = await CreateLoadout();
        var old = await Connection.AddThunderstoreLibraryPackage("4.1.0");
        await LinkIntoCollection(loadout, old, enabled);
        return (loadout, old.AsLibraryItem());
    }

    [Fact]
    public async Task UpdatingMovesTheLoadoutToTheNewVersionAndItsNewDependency()
    {
        var (loadout, old) = await LoadoutLinking410();

        var outcome = await Installer.UpdateAsync(Newest, old, CancellationToken.None);

        Linked(loadout).Should().Contain("Zehs-REPOLib-4.2.0.zip").And.NotContain("Zehs-REPOLib-4.1.0.zip");
        Linked(loadout).Should().ContainSingle(name => name.Contains("BepInExPack"));
        outcome.Downloaded.Select(item => item.Name).Should().BeEquivalentTo("Zehs-REPOLib-4.2.0.zip", "BepInEx-BepInExPack-5.4.2305.zip");
        var replacement = outcome.Replacements.Should().ContainSingle().Which;
        replacement.Old.Id.Should().Be(old.Id);
        replacement.New.Name.Should().Be("Zehs-REPOLib-4.2.0.zip");
        replacement.Result.Should().Be(LibraryItemReplacementResult.Success);
        outcome.DownloadFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdatingKeepsTheOldVersionInTheLibrary()
    {
        var (loadout, old) = await LoadoutLinking410();

        await Installer.UpdateAsync(Newest, old, CancellationToken.None);

        InLibrary("Zehs-REPOLib-4.1.0.zip").Should().BeTrue();
        Linked(loadout).Should().NotContain("Zehs-REPOLib-4.1.0.zip");
    }

    [Fact]
    public async Task UpdatingTwoLoadoutsGivesADependencyOnlyToTheOneThatLacksIt()
    {
        var withLoader = await CreateLoadout();
        var withoutLoader = await CreateLoadout();
        var old = await Connection.AddThunderstoreLibraryPackage("4.1.0");
        await LinkIntoCollection(withLoader, old);
        await LinkIntoCollection(withoutLoader, old);
        await Installer.InstallAsync(Loader, withLoader.LoadoutId, CancellationToken.None);
        Linked(withLoader).Should().ContainSingle(name => name.Contains("BepInExPack"));

        await Installer.UpdateAsync(Newest, old.AsLibraryItem(), CancellationToken.None);

        foreach (var loadout in new[] { withLoader, withoutLoader })
        {
            Linked(loadout).Should().Contain("Zehs-REPOLib-4.2.0.zip").And.NotContain("Zehs-REPOLib-4.1.0.zip");
            Linked(loadout).Should().ContainSingle(name => name.Contains("BepInExPack"), "each loadout needs the loader once");
        }
    }

    [Fact]
    public async Task UpdatingAVersionInADisabledGroupKeepsItDisabled()
    {
        var (loadout, old) = await LoadoutLinking410(enabled: false);

        var outcome = await Installer.UpdateAsync(Newest, old, CancellationToken.None);

        outcome.Replacements.Should().ContainSingle().Which.Result.Should().Be(LibraryItemReplacementResult.Success);
        Linked(loadout).Should().Contain("Zehs-REPOLib-4.2.0.zip").And.NotContain("Zehs-REPOLib-4.1.0.zip");
        IsEnabled(loadout, "Zehs-REPOLib-4.2.0.zip").Should().BeFalse();
    }

    [Fact]
    public async Task UpdatingAnItemNoLoadoutLinksOnlyAddsTheNewVersionToTheLibrary()
    {
        var loadout = await CreateLoadout();
        var old = await Connection.AddThunderstoreLibraryPackage("4.1.0");

        var outcome = await Installer.UpdateAsync(Newest, old.AsLibraryItem(), CancellationToken.None);

        Linked(loadout).Should().BeEmpty();
        InLibrary("Zehs-REPOLib-4.2.0.zip").Should().BeTrue();
        InLibrary("Zehs-REPOLib-4.1.0.zip").Should().BeTrue();
        outcome.Replacements.Should().ContainSingle().Which.New.Name.Should().Be("Zehs-REPOLib-4.2.0.zip");
    }

    [Fact]
    public async Task UpdatingWhenTheNewVersionIsAlreadyInTheLoadoutStillMovesTheOldLink()
    {
        var (loadout, old) = await LoadoutLinking410();
        await Installer.InstallAsync(Newest, loadout.LoadoutId, CancellationToken.None);

        await Installer.UpdateAsync(Newest, old, CancellationToken.None);

        Linked(loadout).Should().NotContain("Zehs-REPOLib-4.1.0.zip");
        Linked(loadout).Should().ContainSingle(name => name == "Zehs-REPOLib-4.2.0.zip");
    }

    [Fact]
    public async Task ADependencyInADisabledGroupStopsTheUpdateBeforeAnythingIsReplaced()
    {
        var (loadout, old) = await LoadoutLinking410();
        await LinkInsideDisabledGroup(loadout, await AddLoaderToLibrary());

        var act = async () => await Installer.UpdateAsync(Newest, old, CancellationToken.None);

        await act.Should().ThrowAsync<ThunderstorePackageInDisabledGroupException>();
        Linked(loadout).Should().Contain("Zehs-REPOLib-4.1.0.zip").And.NotContain("Zehs-REPOLib-4.2.0.zip");
    }

    // The loader package as the index lists it, put in the library by installing it into a loadout of its own.
    private async Task<LibraryFile.ReadOnly> AddLoaderToLibrary()
    {
        var scratch = await CreateLoadout();
        var outcomes = await Installer.InstallAsync(Loader, scratch.LoadoutId, CancellationToken.None);
        return LibraryFile.Load(Connection.Db, outcomes.Single().Item.Id);
    }

    // Where an install puts a mod: a group under the loadout's own collection, which a replace needs to find.
    private async Task LinkIntoCollection(Loadout.ReadOnly loadout, LibraryFile.ReadOnly file, bool enabled = true)
    {
        var collection = Loadout.Load(Connection.Db, loadout.LoadoutId).MutableCollections().First().CollectionId;
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                Name = file.FileName.ToString(),
                LoadoutId = loadout.LoadoutId,
                ParentId = collection,
                IsDisabled = !enabled,
            },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = file.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private async Task LinkInsideDisabledGroup(Loadout.ReadOnly loadout, LibraryFile.ReadOnly file)
    {
        using var tx = Connection.BeginTransaction();
        var parent = new LoadoutItemGroup.New(tx, out var parentId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, parentId) { Name = "Disabled collection", LoadoutId = loadout.LoadoutId, IsDisabled = true },
        };
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = file.FileName.ToString(), LoadoutId = loadout.LoadoutId, ParentId = parent },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = file.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }
}
