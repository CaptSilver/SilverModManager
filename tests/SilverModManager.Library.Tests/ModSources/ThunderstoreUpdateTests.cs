using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// What the Thunderstore source reports as updates and how it applies them. Every test names a community and a team
/// of its own because the in-memory database is shared by the class.
/// </summary>
public class ThunderstoreUpdateTests(IConnection connection)
{
    private sealed record Arranged(
        string Community,
        string Team,
        IGameData Game,
        ThunderstoreUpdateChecker Checker,
        IThunderstoreInstaller Installer,
        IThunderstoreSource Adder,
        ThunderstoreModSource Source);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N");

    private static IGameData ThunderstoreGameOf(string community)
    {
        var game = Substitute.For<IGameData, IThunderstoreGame>();
        ((IThunderstoreGame)game).ThunderstoreCommunity.Returns(community);
        game.DisplayName.Returns("Game of " + community);
        return game;
    }

    private static ThunderstoreCatalog CatalogWith(string community, string team, params (string Name, string[] Versions)[] packages)
    {
        var listings = packages.Select(package => new PackageListing
        {
            Name = package.Name,
            FullName = $"{team}-{package.Name}",
            Owner = team,
            Versions = package.Versions
                .Select(version => new PackageVersion
                {
                    Name = package.Name,
                    FullName = $"{team}-{package.Name}-{version}",
                    VersionNumber = version,
                    DownloadUrl = $"https://thunderstore.io/package/download/{team}/{package.Name}/{version}/",
                    DateCreated = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                })
                .Reverse()
                .ToArray(),
        });
        return new ThunderstoreCatalog(community, listings);
    }

    private Arranged Arrange()
    {
        var community = Unique("community-");
        var game = ThunderstoreGameOf(community);
        var checker = new ThunderstoreUpdateChecker(connection);
        var installer = Substitute.For<IThunderstoreInstaller>();
        var adder = Substitute.For<IThunderstoreSource>();
        return new Arranged(community, Unique("Team"), game, checker, installer, adder,
            ThunderstoreModSources.Create(connection, checker: checker, installer: installer, source: adder, games: game));
    }

    private Task<LibraryFile.ReadOnly> AddFile(Arranged arranged, string name, string version)
        => new LockFixtures(connection).AddThunderstoreFile(arranged.Team, name, version, "ts " + Guid.NewGuid(), null, community: arranged.Community);

    private static Optional<ModUpdate> Latest(IObservable<Optional<ModUpdate>> observable)
    {
        var latest = Optional<ModUpdate>.None;
        using var _ = observable.Subscribe(value => latest = value);
        return latest;
    }

    [Fact]
    public async Task AnItemWithANewerVersionInTheCatalogHasAnUpdateToIt()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        var update = Latest(arranged.Source.ObserveUpdate(file.AsLibraryItem()));

        update.HasValue.Should().BeTrue();
        update.Value.NewestVersion.Should().Be("1.1.0");
        update.Value.CurrentVersion.Should().Be("1.0.0");
        update.Value.Count.Should().Be(1);
        update.Value.CurrentItems.Should().Equal(file.AsLibraryItem().LibraryItemId);
        update.Value.PublishedAt.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }

    [Fact]
    public async Task AnItemAlreadyOnTheNewestVersionHasNoUpdate()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.1.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        Latest(arranged.Source.ObserveUpdate(file.AsLibraryItem())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task AFrozenItemHasNoUpdate()
    {
        var arranged = Arrange();
        var file = await new LockFixtures(connection).AddThunderstoreFile(arranged.Team, "Pkg", "1.0.0", "ts " + Guid.NewGuid(), null, community: arranged.Community,
            extend: (tx, newFile) => _ = new UpdatePin.New(tx, newFile.Id) { Frozen = true });
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        Latest(arranged.Source.ObserveUpdate(file.AsLibraryItem())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ARefreshAfterSubscribingReachesTheObserver()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.0.0");
        var seen = new List<Optional<ModUpdate>>();
        using var subscription = arranged.Source.ObserveUpdate(file.AsLibraryItem()).Subscribe(seen.Add);

        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        seen.Should().ContainSingle().Which.Value.NewestVersion.Should().Be("1.1.0");
    }

    [Fact]
    public async Task ALocalFileHasNoUpdateToObserve()
    {
        var arranged = Arrange();
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + Guid.NewGuid(), null);

        Latest(arranged.Source.ObserveUpdate(local.AsLibraryItem())).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task ThePackageOfSeveralDownloadedVersionsHasOneUpdateToTheNewestVersion()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.0.5", "1.1.0"])));

        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), newer.AsLibraryItem()]));

        update.HasValue.Should().BeTrue();
        update.Value.NewestVersion.Should().Be("1.1.0");
        update.Value.CurrentVersion.Should().Be("1.0.5");
        update.Value.Count.Should().Be(2);
        update.Value.CurrentItems.Should().BeEquivalentTo([older.AsLibraryItem().LibraryItemId, newer.AsLibraryItem().LibraryItemId]);
    }

    [Fact]
    public async Task ThePackageRowLeavesOutTheVersionsThatAreUpToDate()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var current = await AddFile(arranged, "Pkg", "1.1.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), current.AsLibraryItem()]));

        update.Value.CurrentItems.Should().Equal(older.AsLibraryItem().LibraryItemId);
    }

    [Fact]
    public async Task ThePackageRowHasNoUpdateWhenNoVersionHasOne()
    {
        var arranged = Arrange();
        var current = await AddFile(arranged, "Pkg", "1.1.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));

        Latest(arranged.Source.ObserveUpdate([current.AsLibraryItem()])).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task TheSourceOwnsItsOwnUpdatesAndNotNexuss()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));
        var update = Latest(arranged.Source.ObserveUpdate(file.AsLibraryItem())).Value;

        arranged.Source.Owns(update).Should().BeTrue();
        NexusModSources.Create(connection).Owns(update).Should().BeFalse();
        new ModSourceResolver([NexusModSources.Create(connection), arranged.Source]).For(update).Should().BeSameAs(arranged.Source);
    }

    [Fact]
    public async Task AllUpdatesIsOnePerPackageOfTheGamesCommunity()
    {
        var arranged = Arrange();
        var a1 = await AddFile(arranged, "A", "1.0.0");
        var a2 = await AddFile(arranged, "A", "1.0.1");
        var b = await AddFile(arranged, "B", "2.0.0");
        var upToDate = await AddFile(arranged, "C", "3.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team,
            ("A", ["1.0.0", "1.0.1", "1.1.0"]), ("B", ["2.0.0", "2.1.0"]), ("C", ["3.0.0"])));

        var updates = arranged.Source.AllUpdates(arranged.Game);

        updates.Select(update => update.NewestVersion).Should().BeEquivalentTo("1.1.0", "2.1.0");
        updates.Single(update => update.NewestVersion == "1.1.0").CurrentItems.Should().BeEquivalentTo([a1.AsLibraryItem().LibraryItemId, a2.AsLibraryItem().LibraryItemId]);
        updates.Single(update => update.NewestVersion == "2.1.0").CurrentItems.Should().Equal(b.AsLibraryItem().LibraryItemId);
        updates.SelectMany(update => update.CurrentItems).Should().NotContain(upToDate.AsLibraryItem().LibraryItemId);
    }

    [Fact]
    public async Task AllUpdatesOfAGameInAnotherCommunityIsEmpty()
    {
        var arranged = Arrange();
        await AddFile(arranged, "A", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("A", ["1.0.0", "1.1.0"])));

        arranged.Source.AllUpdates(ThunderstoreGameOf(Unique("other-"))).Should().BeEmpty();
    }

    [Fact]
    public void AllUpdatesIsEmptyBeforeTheFirstCheck()
    {
        var arranged = Arrange();

        arranged.Source.AllUpdates(arranged.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task HasAnyUpdatesFollowsEachCheckOfTheGamesCommunity()
    {
        var arranged = Arrange();
        await AddFile(arranged, "A", "1.0.0");
        var seen = new List<bool>();
        using var subscription = arranged.Source.ObserveHasAnyUpdates(arranged.Game).Subscribe(seen.Add);

        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("A", ["1.0.0", "1.1.0"])));
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("A", ["1.0.0"])));

        seen.Should().Equal(false, true, false);
    }

    [Fact]
    public void HasAnyUpdatesIsFalseAtOnceForAGameWithoutThunderstoreSupport()
    {
        var arranged = Arrange();
        bool? seen = null;

        using var subscription = arranged.Source.ObserveHasAnyUpdates(Substitute.For<IGameData>()).Subscribe(has => seen = has);

        seen.Should().BeFalse();
        arranged.Source.AllUpdates(Substitute.For<IGameData>()).Should().BeEmpty();
    }

    private async Task<(Arranged Arranged, ModUpdate Update, LibraryFile.ReadOnly Old)> ArrangeUpdate()
    {
        var arranged = Arrange();
        var old = await AddFile(arranged, "Pkg", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));
        return (arranged, Latest(arranged.Source.ObserveUpdate(old.AsLibraryItem())).Value, old);
    }

    private static ThunderstoreVersionRef Version(Arranged arranged, string version)
        => new(new ThunderstorePackageRef(arranged.Team, "Pkg"), ThunderstoreVersionNumber.TryParse(version, out var number) ? number : throw new InvalidOperationException(version));

    [Fact]
    public async Task ReplacingAnUpdateHandsTheNewestVersionAndTheCurrentItemToTheInstaller()
    {
        var (arranged, update, old) = await ArrangeUpdate();
        var replacement = new ModUpdateReplacement(old.AsLibraryItem(), old.AsLibraryItem(), LibraryItemReplacementResult.Success);
        arranged.Installer.UpdateAsync(Version(arranged, "1.1.0"), Arg.Is<LibraryItem.ReadOnly>(item => item.Id == old.Id), Arg.Any<CancellationToken>())
            .Returns(ModUpdateOutcome.Nothing with { Replacements = [replacement] });

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.Replacements.Should().Equal(replacement);
        outcome.DownloadFailures.Should().BeEmpty();
        outcome.AccountCannotDownload.Should().BeFalse();
        outcome.OpenedInBrowser.Should().BeEmpty();
        await arranged.Adder.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task ReplacingAPackageUpdatesEachOfItsCurrentItems()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.0.5", "1.1.0"])));
        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), newer.AsLibraryItem()])).Value;
        arranged.Installer.UpdateAsync(default, default, default).ReturnsForAnyArgs(ModUpdateOutcome.Nothing);

        await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], true, true), CancellationToken.None);

        await arranged.Installer.Received(1).UpdateAsync(Version(arranged, "1.1.0"), Arg.Is<LibraryItem.ReadOnly>(item => item.Id == older.Id), Arg.Any<CancellationToken>());
        await arranged.Installer.Received(1).UpdateAsync(Version(arranged, "1.1.0"), Arg.Is<LibraryItem.ReadOnly>(item => item.Id == newer.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancellingStopsBeforeTheNextVersionIsUpdated()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.0.5", "1.1.0"])));
        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), newer.AsLibraryItem()])).Value;
        using var cancellation = new CancellationTokenSource();
        arranged.Installer.UpdateAsync(default, default, default).ReturnsForAnyArgs<ValueTask<ModUpdateOutcome>>(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], true, true), cancellation.Token);

        await arranged.Installer.ReceivedWithAnyArgs(1).UpdateAsync(default, default, default);
        outcome.DownloadFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingKeepsTheOldVersionStopsBeforeTheDownload()
    {
        var (arranged, update, _) = await ArrangeUpdate();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], false, true), cancellation.Token);

        await arranged.Adder.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        outcome.DownloadFailures.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedReplaceIsAFailureForThatUpdateAndTheRestStillApply()
    {
        var arranged = Arrange();
        var broken = await AddFile(arranged, "Broken", "1.0.0");
        var fine = await AddFile(arranged, "Fine", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Broken", ["1.0.0", "1.1.0"]), ("Fine", ["1.0.0", "1.1.0"])));
        var updates = arranged.Source.AllUpdates(arranged.Game);
        var failure = new InvalidOperationException("download failed");
        arranged.Installer.UpdateAsync(Arg.Is<ThunderstoreVersionRef>(version => version.Package.Name == "Broken"), Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var replacement = new ModUpdateReplacement(fine.AsLibraryItem(), fine.AsLibraryItem(), LibraryItemReplacementResult.Success);
        arranged.Installer.UpdateAsync(Arg.Is<ThunderstoreVersionRef>(version => version.Package.Name == "Fine"), Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<CancellationToken>())
            .Returns(ModUpdateOutcome.Nothing with { Replacements = [replacement] });

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest(updates, true, false), CancellationToken.None);

        outcome.Replacements.Should().Equal(replacement);
        var failed = outcome.DownloadFailures.Should().ContainSingle().Which;
        failed.Error.Should().BeSameAs(failure);
        failed.Name.Should().Contain("Broken");
        failed.Update.CurrentItems.Should().Equal(broken.AsLibraryItem().LibraryItemId);
        arranged.Source.Owns(failed.Update).Should().BeTrue();
    }

    [Fact]
    public async Task AFailureIsNarrowedToTheItemThatFailedSoARetryRepeatsOnlyThat()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.0.5", "1.1.0"])));
        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), newer.AsLibraryItem()])).Value;
        arranged.Installer.UpdateAsync(default, default, default).ReturnsForAnyArgs(ModUpdateOutcome.Nothing);
        arranged.Installer.UpdateAsync(Arg.Any<ThunderstoreVersionRef>(), Arg.Is<LibraryItem.ReadOnly>(item => item.Id == newer.Id), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], true, true), CancellationToken.None);

        outcome.DownloadFailures.Should().ContainSingle().Which.Update.CurrentItems.Should().Equal(newer.AsLibraryItem().LibraryItemId);
    }

    [Fact]
    public async Task KeepingTheOldVersionAddsTheNewOneToTheLibraryAndLinksNothing()
    {
        var (arranged, update, _) = await ArrangeUpdate();
        var added = await new LockFixtures(connection).AddThunderstoreFile(arranged.Team, "Pkg", "1.1.0", "ts " + Guid.NewGuid(), null, community: arranged.Community);
        ThunderstoreInstallRequest? request = null;
        arranged.Adder.AddAsync(Arg.Do<ThunderstoreInstallRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<LibraryFile.ReadOnly>>([added]));

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], Replace: false, MayOpenBrowser: true), CancellationToken.None);

        request.Should().NotBeNull();
        request!.Root.Should().Be(Version(arranged, "1.1.0"));
        request.WithDependencies.Should().BeTrue();
        request.InstalledInLoadout.Should().BeEmpty();
        ((IThunderstoreGame)request.Game).ThunderstoreCommunity.Should().Be(arranged.Community);
        outcome.Downloaded.Select(item => item.Id).Should().Equal(added.Id);
        outcome.Replacements.Should().BeEmpty();
        await arranged.Installer.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
    }

    [Fact]
    public async Task KeepingTheOldVersionOfAPackageAddsTheNewVersionOnce()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.0.5", "1.1.0"])));
        var update = Latest(arranged.Source.ObserveUpdate([older.AsLibraryItem(), newer.AsLibraryItem()])).Value;
        arranged.Adder.AddAsync(default!, default).ReturnsForAnyArgs(ValueTask.FromResult<IReadOnlyList<LibraryFile.ReadOnly>>([]));

        await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], false, true), CancellationToken.None);

        await arranged.Adder.Received(1).AddAsync(Arg.Any<ThunderstoreInstallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KeepingTheOldVersionThatFailsToDownloadIsAFailureForTheUpdate()
    {
        var (arranged, update, old) = await ArrangeUpdate();
        arranged.Adder.AddAsync(default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("offline"));

        var outcome = await arranged.Source.ApplyUpdates(new ModUpdateRequest([update], false, true), CancellationToken.None);

        var failed = outcome.DownloadFailures.Should().ContainSingle().Which;
        failed.Error.Should().BeOfType<HttpRequestException>();
        failed.Update.CurrentItems.Should().Equal(old.AsLibraryItem().LibraryItemId);
    }

    [Fact]
    public async Task AnUpdateForACommunityNoGameServesIsAFailureNotACrash()
    {
        var (arranged, update, _) = await ArrangeUpdate();
        var source = ThunderstoreModSources.Create(connection, checker: arranged.Checker, installer: arranged.Installer, source: arranged.Adder);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([update], false, true), CancellationToken.None);

        var error = outcome.DownloadFailures.Should().ContainSingle().Which.Error;
        error.Should().BeOfType<NoThunderstoreGameException>().Which.Message.Should().Contain(arranged.Community);
    }

    [Fact]
    public void TheGameOfACommunityIsTheOneThatServesIt()
    {
        var mine = ThunderstoreGameOf("mine");
        var other = ThunderstoreGameOf("other");
        IGameData[] games = [Substitute.For<IGameData>(), other, mine];

        ThunderstoreGame.ForCommunity(games, "mine").Should().BeSameAs(mine);
        ThunderstoreGame.ForCommunity(games, "nobody").Should().BeNull();
    }

    [Fact]
    public async Task OnlyAReplaceSwapsFilesInLoadouts()
    {
        var (arranged, update, _) = await ArrangeUpdate();

        arranged.Source.WillReplace(new ModUpdateRequest([update], Replace: true, MayOpenBrowser: false)).Should().BeTrue();
        arranged.Source.WillReplace(new ModUpdateRequest([update], Replace: false, MayOpenBrowser: false)).Should().BeFalse();
    }

    private static HideUpdatesState LatestHide(IObservable<HideUpdatesState> observable)
    {
        HideUpdatesState latest = default;
        using var _ = observable.Subscribe(value => latest = value);
        return latest;
    }

    private async Task<(Arranged Arranged, LibraryFile.ReadOnly File)> ArrangeWithUpdate()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.0.0");
        arranged.Checker.Refresh(CatalogWith(arranged.Community, arranged.Team, ("Pkg", ["1.0.0", "1.1.0"])));
        return (arranged, file);
    }

    [Fact]
    public async Task FreezingAnItemWritesTheUpdatePin()
    {
        var (arranged, file) = await ArrangeWithUpdate();

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);

        UpdatePin.IsFrozen(connection.Db, file.Id).Should().BeTrue();
    }

    [Fact]
    public async Task FreezingTakesTheUpdateAwayFromAnObserverWithoutANewCheck()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        var seen = new List<Optional<ModUpdate>>();
        using var subscription = arranged.Source.ObserveUpdate(file.AsLibraryItem()).Subscribe(seen.Add);
        seen.Last().HasValue.Should().BeTrue();

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);

        seen.Last().HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task FreezingTakesTheUpdateAwayFromThePackageRow()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        var seen = new List<Optional<ModUpdate>>();
        using var subscription = arranged.Source.ObserveUpdate([file.AsLibraryItem()]).Subscribe(seen.Add);
        seen.Last().HasValue.Should().BeTrue();

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);

        seen.Last().HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task UnfreezingBringsTheUpdateBack()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        var seen = new List<Optional<ModUpdate>>();
        using var subscription = arranged.Source.ObserveUpdate(file.AsLibraryItem()).Subscribe(seen.Add);
        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: false, CancellationToken.None);

        UpdatePin.IsFrozen(connection.Db, file.Id).Should().BeFalse();
        seen.Last().Value.NewestVersion.Should().Be("1.1.0");
    }

    [Fact]
    public async Task FreezingAFrozenItemAgainAndUnfreezingAnUnfrozenOneChangeNothing()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        var items = new[] { file.AsLibraryItem() };

        await arranged.Source.SetUpdatesHidden(items, hidden: false, CancellationToken.None);
        UpdatePin.IsFrozen(connection.Db, file.Id).Should().BeFalse();

        await arranged.Source.SetUpdatesHidden(items, hidden: true, CancellationToken.None);
        await arranged.Source.SetUpdatesHidden(items, hidden: true, CancellationToken.None);
        UpdatePin.IsFrozen(connection.Db, file.Id).Should().BeTrue();

        await arranged.Source.SetUpdatesHidden(items, hidden: false, CancellationToken.None);
        UpdatePin.IsFrozen(connection.Db, file.Id).Should().BeFalse();
    }

    [Fact]
    public async Task FreezingAPackageFreezesEveryVersionOfIt()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");

        await arranged.Source.SetUpdatesHidden([older.AsLibraryItem(), newer.AsLibraryItem()], hidden: true, CancellationToken.None);

        UpdatePin.IsFrozen(connection.Db, older.Id).Should().BeTrue();
        UpdatePin.IsFrozen(connection.Db, newer.Id).Should().BeTrue();
    }

    [Fact]
    public async Task FreezingLeavesAnItemThunderstoreDoesNotOwnAlone()
    {
        var arranged = Arrange();
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + Guid.NewGuid(), null);

        await arranged.Source.SetUpdatesHidden([local.AsLibraryItem()], hidden: true, CancellationToken.None);

        UpdatePin.IsFrozen(connection.Db, local.Id).Should().BeFalse();
    }

    [Fact]
    public async Task TheFreezeActionIsEnabledAndVisibleWithNoUpdatePending()
    {
        var arranged = Arrange();
        var file = await AddFile(arranged, "Pkg", "1.1.0");

        LatestHide(arranged.Source.ObserveHideUpdates([file.AsLibraryItem()]))
            .Should().Be(new HideUpdatesState(IsHidden: false, ItemCount: 1, IsEnabled: true, IsVisible: true));
    }

    [Fact]
    public async Task TheFrozenRowStillShowsTheActionAndOffersToUnfreeze()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        var states = new List<HideUpdatesState>();
        using var subscription = arranged.Source.ObserveHideUpdates([file.AsLibraryItem()]).Subscribe(states.Add);

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);

        states.Last().Should().Be(new HideUpdatesState(IsHidden: true, ItemCount: 1, IsEnabled: true, IsVisible: true));
        Latest(arranged.Source.ObserveUpdate(file.AsLibraryItem())).HasValue.Should().BeFalse("the frozen item has no update, yet its action is still there");
    }

    [Fact]
    public async Task UnfreezingPutsTheActionBackToFreeze()
    {
        var (arranged, file) = await ArrangeWithUpdate();
        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: true, CancellationToken.None);
        var states = new List<HideUpdatesState>();
        using var subscription = arranged.Source.ObserveHideUpdates([file.AsLibraryItem()]).Subscribe(states.Add);
        states.Last().IsHidden.Should().BeTrue();

        await arranged.Source.SetUpdatesHidden([file.AsLibraryItem()], hidden: false, CancellationToken.None);

        states.Last().IsHidden.Should().BeFalse();
    }

    [Fact]
    public async Task APackageRowIsFrozenOnlyWhenEveryVersionIs()
    {
        var arranged = Arrange();
        var older = await AddFile(arranged, "Pkg", "1.0.0");
        var newer = await AddFile(arranged, "Pkg", "1.0.5");
        var items = new[] { older.AsLibraryItem(), newer.AsLibraryItem() };
        var states = new List<HideUpdatesState>();
        using var subscription = arranged.Source.ObserveHideUpdates(items).Subscribe(states.Add);

        await arranged.Source.SetUpdatesHidden([older.AsLibraryItem()], hidden: true, CancellationToken.None);
        states.Last().Should().Be(new HideUpdatesState(IsHidden: false, ItemCount: 2, IsEnabled: true, IsVisible: true));

        await arranged.Source.SetUpdatesHidden([newer.AsLibraryItem()], hidden: true, CancellationToken.None);
        states.Last().IsHidden.Should().BeTrue();
    }

    [Fact]
    public async Task TheFreezeActionIsDisabledAndInvisibleForAnItemThunderstoreDoesNotOwn()
    {
        var arranged = Arrange();
        var local = await new LockFixtures(connection).AddLibraryFile("local.zip", "local " + Guid.NewGuid(), null);

        var state = LatestHide(arranged.Source.ObserveHideUpdates([local.AsLibraryItem()]));

        state.IsEnabled.Should().BeFalse();
        state.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void TheFreezeActionIsLabelledFreezeAndUnfreezeVersion()
    {
        var source = Arrange().Source;

        source.HideUpdatesLabel(hidden: false).Should().Be(HideUpdatesLabelKind.Freeze);
        source.HideUpdatesLabel(hidden: true).Should().Be(HideUpdatesLabelKind.Unfreeze);
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
