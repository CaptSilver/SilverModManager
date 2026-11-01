using NSubstitute;
using SilverModManager.Networking.Thunderstore;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Backend;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

public class ModSourceResolverTests(IConnection connection)
{
    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 71000;

    private ModSourceResolver Resolver() => new([NexusModSources.Create(connection, Substitute.For<IModUpdateService>())]);

    [Fact]
    public async Task ANexusItemResolvesToTheNexusSource()
    {
        var fixtures = new LockFixtures(connection);
        var id = _nextId++;
        var file = await fixtures.AddNexusFile("mod.zip", "nexus " + id, null, "A mod", "1.0", id, id);

        var source = Resolver().For(file.AsLibraryItem());

        source.Should().BeOfType<NexusModSource>();
        source!.DisplayName.Should().Be("Nexus Mods");
    }

    [Fact]
    public async Task ANexusItemsPageIsTheModPageOnTheSite()
    {
        var fixtures = new LockFixtures(connection);
        var id = _nextId++;
        var file = await fixtures.AddNexusFile("mod.zip", "nexus " + id, null, "A mod", "1.0", id, id);
        var expected = NexusModsUrlBuilder.GetModUri(GameDomain.From("eldenring"), ModId.From(id));

        var source = Resolver().For(file.AsLibraryItem())!;

        source.PageUri(file.AsLibraryItem()).Should().Be(expected);
    }

    [Fact]
    public async Task ANexusItemsChangelogIsTheModPageBecauseTheSiteHasNoPerFileChangelog()
    {
        var fixtures = new LockFixtures(connection);
        var id = _nextId++;
        var file = await fixtures.AddNexusFile("mod.zip", "nexus " + id, null, "A mod", "1.0", id, id);
        var expected = NexusModsUrlBuilder.GetModUri(GameDomain.From("eldenring"), ModId.From(id));

        var source = Resolver().For(file.AsLibraryItem())!;

        source.ChangelogUri(file.AsLibraryItem()).Should().Be(expected);
    }

    [Fact]
    public async Task ALocalFileHasNoSource()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddLibraryFile("local.zip", "local " + _nextId++, null);

        Resolver().For(file.AsLibraryItem()).Should().BeNull();
    }

    [Fact]
    public async Task TheNexusSourceDoesNotOwnALocalFileAndHasNoPageForIt()
    {
        var fixtures = new LockFixtures(connection);
        var file = await fixtures.AddLibraryFile("local.zip", "local " + _nextId++, null);
        var source = NexusModSources.Create(connection, Substitute.For<IModUpdateService>());

        source.Owns(file.AsLibraryItem()).Should().BeFalse();
        source.PageUri(file.AsLibraryItem()).Should().BeNull();
        source.ChangelogUri(file.AsLibraryItem()).Should().BeNull();
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
