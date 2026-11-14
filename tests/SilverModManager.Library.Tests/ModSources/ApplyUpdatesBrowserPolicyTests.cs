using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Thunderstore;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// What every mod source owes Update All: a request with <see cref="ModUpdateRequest.MayOpenBrowser"/> false opens no
/// tab, so forty updates that can only be fetched through a website never become forty tabs. A source derives from this
/// with an update it would normally send to the browser (or fetch itself, when it never uses one).
/// </summary>
public abstract class ModSourceBrowserPolicyContract(IConnection connection)
{
    protected IConnection Connection { get; } = connection;

    /// <summary>
    /// A source with one applicable update, and a way to read back every link the app handed to the OS.
    /// </summary>
    protected abstract Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange();

    [Fact]
    public async Task UpdateAllNeverOpensTheBrowser()
    {
        var (source, update, openedByOs) = await Arrange();

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: false), CancellationToken.None);

        outcome.OpenedInBrowser.Should().BeEmpty();
        openedByOs().Should().BeEmpty();
    }
}

public class NexusUpdateAllBrowserPolicyTests(IConnection connection) : ModSourceBrowserPolicyContract(connection)
{
    private static uint _nextId = 74000;

    protected override async Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange()
    {
        var id = _nextId++;
        var library = await new LockFixtures(Connection).AddNexusFile("policy.zip", "policy " + id, null, "Policy", "1.0", id, id);
        var current = NexusModsLibraryItem.Load(Connection.Db, library.Id).FileMetadata;
        using var tx = Connection.BeginTransaction();
        var newer = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(_nextId++), LockFixtures.GameId),
            Name = "policy-newer.zip",
            Version = "2.0",
            UploadedAt = DateTimeOffset.UnixEpoch.AddDays(10).UtcDateTime,
            ModPageId = current.ModPageId,
        };
        var newerMetadata = (await tx.Commit()).Remap(newer);

        // A free account is the case a browser would open for.
        var login = Substitute.For<ILoginManager>();
        login.IsPremium.Returns(false);
        var os = Substitute.For<IOSInterop>();
        var opened = new List<Uri>();
        os.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call => opened.Add(call.Arg<Uri>()));
        var update = NexusModUpdate.From(new ModUpdatesOnModPage([new ModUpdateOnPage(current, [newerMetadata])]))!;
        return (NexusModSources.Create(Connection, login: login, os: os), update, () => opened);
    }

    [Fact]
    public async Task AFreeAccountIsToldItCannotDownloadInsteadOfBeingSentToTheSite()
    {
        var (source, update, _) = await Arrange();

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: false), CancellationToken.None);

        outcome.AccountCannotDownload.Should().BeTrue();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}

public class ThunderstoreUpdateAllBrowserPolicyTests(IConnection connection) : ModSourceBrowserPolicyContract(connection)
{
    protected override async Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange()
    {
        var community = "community-" + Guid.NewGuid().ToString("N");
        var team = "Team" + Guid.NewGuid().ToString("N");
        var game = Substitute.For<IGameData, IThunderstoreGame>();
        ((IThunderstoreGame)game).ThunderstoreCommunity.Returns(community);
        var checker = new ThunderstoreUpdateChecker(Connection);
        var installer = Substitute.For<IThunderstoreInstaller>();
        installer.UpdateAsync(default, default, default).ReturnsForAnyArgs(ModUpdateOutcome.Nothing);
        var file = await new LockFixtures(Connection).AddThunderstoreFile(team, "Pkg", "1.0.0", "ts " + Guid.NewGuid(), null, community: community);
        var versions = new[] { "1.0.0", "1.1.0" }.Select(version => new PackageVersion
        {
            Name = "Pkg",
            FullName = $"{team}-Pkg-{version}",
            VersionNumber = version,
            DownloadUrl = $"https://thunderstore.io/package/download/{team}/Pkg/{version}/",
        }).Reverse().ToArray();
        checker.Refresh(new ThunderstoreCatalog(community, [new PackageListing { Name = "Pkg", FullName = $"{team}-Pkg", Owner = team, Versions = versions }]));
        var source = ThunderstoreModSources.Create(Connection, checker: checker, installer: installer, games: game);

        ModUpdate? update = null;
        using (source.ObserveUpdate(file.AsLibraryItem()).Subscribe(value => update = value.HasValue ? value.Value : null)) { }
        return (source, update!, () => []);
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels().AddGameInstallMetadataModel().AddLoadoutAbstractions().AddOSInterop().AddFileExtractors();
        }
    }
}
