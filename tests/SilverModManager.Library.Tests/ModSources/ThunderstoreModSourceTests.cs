using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using NSubstitute;
using SilverModManager.Networking.Thunderstore;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

public class ThunderstoreModSourceTests(IConnection connection)
{
    private ModSourceResolver Resolver() => new([new NexusModSource(Substitute.For<IModUpdateService>()), new ThunderstoreModSource(Substitute.For<IThunderstoreCatalogSource>(), new ThunderstoreUpdateChecker(connection))]);

    [Fact]
    public async Task AThunderstoreItemResolvesToTheThunderstoreSource()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.0", "ts " + Guid.NewGuid(), null);

        var source = Resolver().For(file.AsLibraryItem());

        source.Should().BeOfType<ThunderstoreModSource>();
        source!.DisplayName.Should().Be("Thunderstore");
    }

    [Fact]
    public async Task AThunderstoreItemsPageIsThePackagePageInItsCommunity()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.0", "ts " + Guid.NewGuid(), null, community: "repo");

        new ThunderstoreModSource(Substitute.For<IThunderstoreCatalogSource>(), new ThunderstoreUpdateChecker(connection)).PageUri(file.AsLibraryItem()).Should().Be(new Uri("https://thunderstore.io/c/repo/p/Zehs/REPOLib/"));
    }

    [Fact]
    public async Task AThunderstoreItemsChangelogIsThePackagesChangelogPage()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.0", "ts " + Guid.NewGuid(), null, community: "valheim");

        new ThunderstoreModSource(Substitute.For<IThunderstoreCatalogSource>(), new ThunderstoreUpdateChecker(connection)).ChangelogUri(file.AsLibraryItem()).Should().Be(new Uri("https://thunderstore.io/c/valheim/p/Zehs/REPOLib/changelog/"));
    }

    [Fact]
    public async Task TheThunderstoreSourceDoesNotOwnALocalFileAndHasNoPageForIt()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddLibraryFile("local.zip", "local " + Guid.NewGuid(), null);
        var source = new ThunderstoreModSource(Substitute.For<IThunderstoreCatalogSource>(), new ThunderstoreUpdateChecker(connection));

        source.Owns(file.AsLibraryItem()).Should().BeFalse();
        source.PageUri(file.AsLibraryItem()).Should().BeNull();
        source.ChangelogUri(file.AsLibraryItem()).Should().BeNull();
        Resolver().For(file.AsLibraryItem()).Should().BeNull();
    }

    [Fact]
    public async Task TheNexusSourceDoesNotOwnAThunderstoreItem()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddThunderstoreFile("Zehs", "REPOLib", "4.2.0", "ts " + Guid.NewGuid(), null);

        new NexusModSource(Substitute.For<IModUpdateService>()).Owns(file.AsLibraryItem()).Should().BeFalse();
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
