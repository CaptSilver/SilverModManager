using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.Lock;

/// <summary>Which links count as the user already having a locked file when a lock is imported into a loadout.</summary>
public class LockImportOwnershipTests(IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);

    private sealed class OwningLock : ICollectionLock
    {
        private static bool IsList(CollectionGroup.ReadOnly collection)
            => collection.AsLoadoutItemGroup().AsLoadoutItem().Name.StartsWith("List:", StringComparison.Ordinal);

        public string? WhyLocked(CollectionGroup.ReadOnly collection) => IsList(collection) ? "a list decides what it contains" : null;
        public bool OwnsItsMods(CollectionGroup.ReadOnly collection) => IsList(collection);
    }

    private static CollectionOwnership Ownership => new([new OwningLock()]);

    [Fact]
    public async Task AnArchiveOnlyAModlistLinksIsNotTheUsersOwnCopy()
    {
        var loadout = await _fixtures.CreateLoadout();
        var list = await _fixtures.AddCollection(loadout, "List: Yggdrasil");
        var file = await _fixtures.AddLibraryFile("shared.zip", "shared " + Guid.NewGuid(), LockFixtures.DigestOf("shared"));
        await _fixtures.LinkUnder(loadout, file, "list mod", list);

        LockVerbs.IsInLoadoutOfTheUser(connection.Db, Ownership, file.AsLibraryItem(), loadout.LoadoutId)
            .Should().BeFalse("the list's copy belongs to the list, so the import installs the user their own");
    }

    [Fact]
    public async Task AnArchiveTheUserLinkedAlongsideAModlistIsTheUsersOwnCopy()
    {
        var loadout = await _fixtures.CreateLoadout();
        var list = await _fixtures.AddCollection(loadout, "List: Yggdrasil");
        var file = await _fixtures.AddLibraryFile("both.zip", "both " + Guid.NewGuid(), LockFixtures.DigestOf("both"));
        await _fixtures.LinkUnder(loadout, file, "list mod", list);
        await _fixtures.Link(loadout, file, "user mod");

        LockVerbs.IsInLoadoutOfTheUser(connection.Db, Ownership, file.AsLibraryItem(), loadout.LoadoutId).Should().BeTrue();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddGameInstallMetadataModel()
                .AddLoadoutAbstractions()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}
