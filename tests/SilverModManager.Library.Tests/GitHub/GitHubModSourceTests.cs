using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Sdk;
using SilverModManager.Library.GitHub;
using SilverModManager.Library.Listing;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.Library.Tests.ModSources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

internal static class GitHubModSources
{
    public static GitHubModSource Create(ShelfRig rig, IConnection connection, IServiceProvider provider, ILibraryService? library = null)
    {
        var services = new ServiceCollection().AddSingleton(library ?? Substitute.For<ILibraryService>()).BuildServiceProvider();
        return new GitHubModSource(rig.Releases, rig.Entries, connection, rig.Api, () => rig.Client, new CompositeServiceProvider(services, provider), NullLogger<GitHubModSource>.Instance);
    }

    public static async Task<LibraryItem.ReadOnly> AddItem(IConnection connection, string asset, string tag, string owner = "o", string repository = "tool", ulong? repositoryId = null)
    {
        var content = "content " + Guid.NewGuid();
        var file = await new LockFixtures(connection).AddGitHubFile(asset, content, LockFixtures.DigestOf(content), tag, owner, repository, repositoryId);
        return file.AsLibraryItem();
    }
}

internal sealed class CompositeServiceProvider(IServiceProvider overrides, IServiceProvider host) : IServiceProvider
{
    public object? GetService(Type serviceType) => overrides.GetService(serviceType) ?? host.GetService(serviceType);
}

public class GitHubModSourceTests(IServiceProvider serviceProvider, IConnection connection)
{
    // The tests share a database, so each one lists a repository of its own: another test's row for "o/tool" would
    // otherwise decide this test's prerelease setting.
    private readonly string _repo = "tool" + Guid.NewGuid().ToString("N")[..8];
    private GitHubShelfEntryDefinition Tool => new("o", _repo, ".7z", "tool_", "Tool");

    private static Optional<ModUpdate> LatestOf(IObservable<Optional<ModUpdate>> observed)
    {
        var seen = Optional<ModUpdate>.None;
        using (observed.Subscribe(value => seen = value)) { }
        return seen;
    }

    private ShelfRig Arrange(IConnection connection, string latest = "v2", params GitHubShelfEntryDefinition[] entries)
    {
        var rig = new ShelfRig(connection, entries.Length == 0 ? [Tool] : entries);
        rig.Api.Marked[$"o/{_repo}"] = TestReleases.Make(latest, "o", _repo, [TestReleases.AssetOf($"tool_{latest}.7z")]);
        return rig;
    }

    [Fact]
    public async Task AnItemAtAnOlderTagHasAnUpdateToTheMarkedLatest()
    {
        var rig = Arrange(connection);
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var update = LatestOf(source.ObserveUpdate(item)).Value;
        update.CurrentVersion.Should().Be("v1");
        update.NewestVersion.Should().Be("v2");
        update.Count.Should().Be(1);
        source.Owns(update).Should().BeTrue();
        source.AllUpdates(rig.Game).Should().ContainSingle();
        bool? any = null;
        using (source.ObserveHasAnyUpdates(rig.Game).Subscribe(value => any = value)) { }
        any.Should().BeTrue();
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("v3")]
    public async Task AnItemAtOrBeyondTheLatestTagHasNoUpdate(string tag)
    {
        var rig = Arrange(connection);
        var item = await GitHubModSources.AddItem(connection, $"tool_{tag}.7z", tag, "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task ATagThatIsNotAVersionIsNeverCalledOlder()
    {
        var rig = Arrange(connection);
        var item = await GitHubModSources.AddItem(connection, "tool_nightly.7z", "nightly-2026-11-01", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AnItemAddedByRepositoryIdIsMatchedToItsEntry()
    {
        var rig = Arrange(connection, "v2", Tool with { RepositoryId = 555UL });
        rig.Api.ById[555UL] = rig.Api.Marked[$"o/{_repo}"];
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", repositoryId: 555UL);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).Value.NewestVersion.Should().Be("v2");
        source.PageUri(item).Should().Be(new Uri($"https://github.com/o/{_repo}/releases/tag/v1"), "the entry knows the name the id stands for");
    }

    [Fact]
    public async Task APrereleaseIsOfferedOnlyWhenTheEntryOptedIn()
    {
        var rig = new ShelfRig(connection);
        rig.Api.Marked[$"o/{_repo}"] = TestReleases.Make("v2", "o", _repo, [TestReleases.AssetOf("tool_v2.7z")]);
        rig.Api.Lists[$"o/{_repo}"] =
        [
            TestReleases.Make("v3-rc1", "o", _repo, [TestReleases.AssetOf("tool_v3-rc1.7z")], prerelease: true),
            TestReleases.Make("v2", "o", _repo, [TestReleases.AssetOf("tool_v2.7z")]),
        ];
        await rig.Entries.Add(rig.GameId, $"o/{_repo}", ".7z", "tool_", includePrereleases: false);
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).Value.NewestVersion.Should().Be("v2");

        var optedIn = new ShelfRig(connection);
        optedIn.Api.Marked[$"o/{_repo}"] = rig.Api.Marked[$"o/{_repo}"];
        optedIn.Api.Lists[$"o/{_repo}"] = rig.Api.Lists[$"o/{_repo}"];
        await optedIn.Entries.Add(optedIn.GameId, $"o/{_repo}", ".7z", "tool_", includePrereleases: true);
        var optedInSource = GitHubModSources.Create(optedIn, connection, serviceProvider);
        await optedInSource.CheckUpdates(optedIn.Game, force: false, CancellationToken.None);

        LatestOf(optedInSource.ObserveUpdate(item)).Value.NewestVersion.Should().Be("v3-rc1");
    }

    [Fact]
    public async Task CheckUpdatesWithoutForceWithinTheHourMakesNoRequest()
    {
        var rig = Arrange(connection);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        var afterFirst = rig.Api.Requests.Count;
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Requests.Should().HaveCount(afterFirst, "entering the page again inside the hour does not ask GitHub");

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        rig.Api.Requests.Count(request => request.StartsWith("latest ")).Should().Be(2, "Refresh asks again");
    }

    [Fact]
    public async Task AUsedUpRateLimitLeavesTheCheckQuietAndKeepsWhatWasKnown()
    {
        var rig = Arrange(connection);
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Failure = new GitHubRateLimitedException(rig.Time.GetUtcNow().AddMinutes(20));

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue("the earlier answer still stands");
    }

    [Fact]
    public async Task HidingAnUpdateHidesItUntilANewerReleaseAppears()
    {
        var rig = Arrange(connection);
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeTrue();

        await source.SetUpdatesHidden([item], hidden: true, CancellationToken.None);

        LatestOf(source.ObserveUpdate(item)).HasValue.Should().BeFalse();
        HideStateOf(source, item).Should().Be(new HideUpdatesState(IsHidden: true, ItemCount: 1, IsEnabled: true, IsVisible: true));
        source.HideUpdatesLabel(hidden: false).Should().Be(HideUpdatesLabelKind.Hide);
        source.HideUpdatesLabel(hidden: true).Should().Be(HideUpdatesLabelKind.Show);

        rig.Api.Marked[$"o/{_repo}"] = TestReleases.Make("v3", "o", _repo, [TestReleases.AssetOf("tool_v3.7z")]);
        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        LatestOf(source.ObserveUpdate(item)).Value.NewestVersion.Should().Be("v3", "a release newer than the hidden one is offered");

        await source.SetUpdatesHidden([item], hidden: false, CancellationToken.None);
        HideStateOf(source, item).IsHidden.Should().BeFalse();
    }

    private static HideUpdatesState HideStateOf(IModSource source, LibraryItem.ReadOnly item)
    {
        HideUpdatesState? state = null;
        using (source.ObserveHideUpdates([item]).Subscribe(value => state = value)) { }
        return state!.Value;
    }

    [Fact]
    public async Task OnlyItemsDownloadedFromAGitHubReleaseAreOwned()
    {
        var rig = Arrange(connection);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);
        var github = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var other = (await new LockFixtures(connection).AddLibraryFile("local.zip", "x" + Guid.NewGuid(), null)).AsLibraryItem();

        source.Owns(github).Should().BeTrue();
        source.Owns(other).Should().BeFalse();
        source.DisplayName.Should().Be("GitHub");
        source.PageUri(github).Should().Be(new Uri($"https://github.com/o/{_repo}/releases/tag/v1"));
        source.ChangelogUri(github).Should().Be(new Uri($"https://github.com/o/{_repo}/releases/tag/v1"));
        source.PageUri(other).Should().BeNull();
    }

    [Fact]
    public async Task ApplyingAnUpdateDownloadsTheNewAssetAndSwapsItIntoTheLoadouts()
    {
        var rig = Arrange(connection);
        var payload = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 2000).Select(i => (byte)(i * 3 + 1))).ToArray();
        rig.Bytes.Serve("https://example.invalid/tool_v2.7z", payload);
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<ReplaceLibraryItemOptions>())
            .Returns(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var source = GitHubModSources.Create(rig, connection, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        var update = source.AllUpdates(rig.Game).Single();

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.DownloadFailures.Should().BeEmpty();
        var downloaded = outcome.Downloaded.Should().ContainSingle().Subject;
        GitHubReleaseLibraryFile.Load(connection.Db, downloaded.Id).Tag.Should().Be("v2");
        outcome.Replacements.Should().ContainSingle().Which.Old.Id.Should().Be(item.Id);
        await library.Received(1).ReplaceLinkedItemsInAllLoadouts(
            Arg.Is<LibraryItem.ReadOnly>(old => old.Id == item.Id), Arg.Is<LibraryItem.ReadOnly>(next => next.Id == downloaded.Id),
            Arg.Is<ReplaceLibraryItemOptions>(options => options == ReplaceLibraryItemOptions.ModUpdate));
    }

    [Fact]
    public async Task ApplyingWithoutReplaceOnlyAddsToTheLibrary()
    {
        var rig = Arrange(connection);
        rig.Bytes.Serve("https://example.invalid/tool_v2.7z", Guid.NewGuid().ToByteArray().Concat(new byte[500]).ToArray());
        await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var library = Substitute.For<ILibraryService>();
        var source = GitHubModSources.Create(rig, connection, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest(source.AllUpdates(rig.Game), Replace: false, MayOpenBrowser: true), CancellationToken.None);

        outcome.Downloaded.Should().ContainSingle();
        outcome.Replacements.Should().BeEmpty();
        await library.DidNotReceiveWithAnyArgs().ReplaceLinkedItemsInAllLoadouts(default, default, default);
    }

    [Fact]
    public async Task AFailedDownloadComesBackInTheOutcomeInsteadOfBeingThrown()
    {
        var rig = Arrange(connection); // the asset of the new release is not served, so the download fails
        await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1", "o", _repo);
        var source = GitHubModSources.Create(rig, connection, serviceProvider);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest(source.AllUpdates(rig.Game), Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.DownloadFailures.Should().ContainSingle();
        outcome.Downloaded.Should().BeEmpty();
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

public class GitHubUpdateAllBrowserPolicyTests(IServiceProvider serviceProvider, IConnection connection) : ModSourceBrowserPolicyContract(connection)
{
    protected override async Task<(IModSource Source, ModUpdate Update, Func<IReadOnlyList<Uri>> OpenedByOs)> Arrange()
    {
        var rig = new ShelfRig(connection, new GitHubShelfEntryDefinition("o", "policy", ".7z", null, "Tool"));
        rig.Api.Marked["o/policy"] = TestReleases.Make("v2", "o", "policy", [TestReleases.AssetOf("policy_v2.7z")]);
        rig.Bytes.Serve("https://example.invalid/policy_v2.7z", Guid.NewGuid().ToByteArray().Concat(new byte[300]).ToArray());
        await GitHubModSources.AddItem(connection, "policy_v1.7z", "v1", "o", "policy");
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(default, default, default).ReturnsForAnyArgs(ValueTask.FromResult(LibraryItemReplacementResult.Success));
        var source = GitHubModSources.Create(rig, connection, serviceProvider, library);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        // Downloads go straight to the app's own HTTP client: nothing here can reach the browser.
        return (source, source.AllUpdates(rig.Game).Single(), () => []);
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

public class GitHubReleaseKindTests(IConnection connection)
{
    private static readonly GitHubReleaseKind Kind = new();

    [Fact]
    public async Task TheSourceColumnSaysGitHubAndTheTofuNote()
    {
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1");
        using (var tx = connection.BeginTransaction())
        {
            tx.Add(item.Id, GitHubReleaseLibraryFile.DigestAdvertised, false);
            await tx.Commit();
        }

        var match = Kind.Match(LibraryItem.Load(connection.Db, item.Id))!;

        match.Host.Should().Be("GitHub");
        match.Version.Should().Be("v1");
        match.Note.Should().Be("pinned on first download, not verified by GitHub");
    }

    [Fact]
    public async Task AnAssetGitHubVouchedForCarriesNoNote()
    {
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1");
        using (var tx = connection.BeginTransaction())
        {
            tx.Add(item.Id, GitHubReleaseLibraryFile.DigestAdvertised, true);
            await tx.Commit();
        }

        Kind.Match(LibraryItem.Load(connection.Db, item.Id))!.Note.Should().BeNull();
    }

    [Fact]
    public async Task ARowAddedBeforeTheDigestWasRecordedCarriesNoNote()
    {
        var item = await GitHubModSources.AddItem(connection, "tool_v1.7z", "v1");

        var match = Kind.Match(LibraryItem.Load(connection.Db, item.Id))!;

        match.Host.Should().Be("GitHub");
        match.Note.Should().BeNull("nothing says how the pin was taken, so nothing is claimed");
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}
