using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;

namespace SilverModManager.Library.Tests.ModSources;

/// <summary>
/// Applying Nexus Mods updates: the downloads, the swap in the loadouts and the links a free account follows in the
/// browser. The downloader, the library service and the OS are substituted; the source decides what to ask of them.
/// </summary>
public class NexusModSourceApplyTests(IConnection connection)
{
    // The host's database is shared by every test in the run, so each test gets mod and file ids of its own.
    private static uint _nextId = 73000;

    private sealed record Installed(LibraryFile.ReadOnly Library, NexusModsFileMetadata.ReadOnly Current);

    private async Task<Installed> AddInstalled(string name)
    {
        var id = _nextId++;
        var library = await new LockFixtures(connection).AddNexusFile(name + ".zip", "nexus " + id, null, name, "1.0", id, id);
        return new Installed(library, NexusModsLibraryItem.Load(connection.Db, library.Id).FileMetadata);
    }

    private async Task<NexusModsFileMetadata.ReadOnly> AddNewer(NexusModsFileMetadata.ReadOnly sibling, string name)
    {
        using var tx = connection.BeginTransaction();
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(_nextId++), LockFixtures.GameId),
            Name = name,
            Version = "2.0",
            UploadedAt = DateTimeOffset.UnixEpoch.AddDays(10).UtcDateTime,
            ModPageId = sibling.ModPageId,
        };
        var result = await tx.Commit();
        return result.Remap(metadata);
    }

    private async Task<LibraryFile.ReadOnly> AddDownloaded(string name)
    {
        var id = _nextId++;
        return await new LockFixtures(connection).AddNexusFile(name + ".zip", "downloaded " + id, null, name, "2.0", id, id);
    }

    private static NexusModUpdate UpdateOf(params (NexusModsFileMetadata.ReadOnly Current, NexusModsFileMetadata.ReadOnly Newer)[] files)
        => NexusModUpdate.From(new ModUpdatesOnModPage(files.Select(file => new ModUpdateOnPage(file.Current, [file.Newer])).ToArray()))!;

    private static ILoginManager Login(bool premium)
    {
        var login = Substitute.For<ILoginManager>();
        login.IsPremium.Returns(premium);
        return login;
    }

    // Maps each newest file to the library file the downloader pretends to have fetched.
    private static INexusUpdateDownloader DownloaderReturning(params (NexusModsFileMetadata.ReadOnly Newest, LibraryFile.ReadOnly Downloaded)[] files)
    {
        var downloader = Substitute.For<INexusUpdateDownloader>();
        downloader.Download(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<IReadOnlyList<NexusModsFileMetadata.ReadOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(files.Single(file => file.Newest.Id == call.Arg<NexusModsFileMetadata.ReadOnly>().Id).Downloaded));
        return downloader;
    }

    private static ILibraryService LibraryReplacing(LibraryItemReplacementResult result = LibraryItemReplacementResult.Success)
    {
        var library = Substitute.For<ILibraryService>();
        library.ReplaceLinkedItemsInAllLoadouts(Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<LibraryItem.ReadOnly>(), Arg.Any<ReplaceLibraryItemOptions>())
            .Returns(ValueTask.FromResult(result));
        return library;
    }

    [Fact]
    public async Task Owns_AnUpdateOnlyWhenItIsANexusUpdate()
    {
        var installed = await AddInstalled("Owned");
        var update = UpdateOf((installed.Current, await AddNewer(installed.Current, "newer.zip")));
        var source = NexusModSources.Create(connection);

        source.Owns(update).Should().BeTrue();
        source.Owns(new OtherUpdate()).Should().BeFalse();
        new ModSourceResolver([source]).For(update).Should().BeSameAs(source);
        new ModSourceResolver([source]).For(new OtherUpdate()).Should().BeNull();
    }

    [Fact]
    public async Task Premium_DownloadsEachNewestFileOnceAcrossPages_AndReplacesEveryCurrentFileIgnoringReadOnlyCollections()
    {
        var first = await AddInstalled("First");
        var second = await AddInstalled("Second");
        var shared = await AddNewer(first.Current, "shared.zip");
        var downloaded = await AddDownloaded("shared");
        var downloader = DownloaderReturning((shared, downloaded));
        var library = LibraryReplacing();
        var source = NexusModSources.Create(connection, login: Login(true), downloader: downloader, library: library);

        var outcome = await source.ApplyUpdates(
            new ModUpdateRequest([UpdateOf((first.Current, shared)), UpdateOf((second.Current, shared))], Replace: true, MayOpenBrowser: true),
            CancellationToken.None);

        await downloader.Received(1).Download(
            Arg.Is<NexusModsFileMetadata.ReadOnly>(file => file.Id == shared.Id),
            Arg.Is<IReadOnlyList<NexusModsFileMetadata.ReadOnly>>(replaced => replaced.Select(file => file.Id).OrderBy(id => id).SequenceEqual(new[] { first.Current.Id, second.Current.Id }.OrderBy(id => id))),
            Arg.Any<CancellationToken>());
        await library.Received(2).ReplaceLinkedItemsInAllLoadouts(
            Arg.Any<LibraryItem.ReadOnly>(),
            Arg.Is<LibraryItem.ReadOnly>(item => item.Id == downloaded.Id),
            Arg.Is<ReplaceLibraryItemOptions>(options => options.IgnoreReadOnlyCollections));
        outcome.Downloaded.Select(item => item.Id).Should().Equal(downloaded.Id);
        outcome.Replacements.Select(replacement => replacement.Old.Id).Should().BeEquivalentTo([first.Library.Id, second.Library.Id]);
        outcome.Replacements.Should().OnlyContain(replacement => replacement.Result == LibraryItemReplacementResult.Success && replacement.New.Id == downloaded.Id);
        outcome.DownloadFailures.Should().BeEmpty();
        outcome.AccountCannotDownload.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task WillReplace_OnlyForAPremiumAccountThatWasAskedToReplace(bool premium, bool replace, bool expected)
    {
        var installed = await AddInstalled("Planned");
        var update = UpdateOf((installed.Current, await AddNewer(installed.Current, "planned-newer.zip")));
        var source = NexusModSources.Create(connection, login: Login(premium));

        source.WillReplace(new ModUpdateRequest([update], replace, MayOpenBrowser: true)).Should().Be(expected);
    }

    [Fact]
    public async Task Premium_WithoutReplace_DownloadsAndReplacesNothing()
    {
        var installed = await AddInstalled("Kept");
        var newer = await AddNewer(installed.Current, "kept-newer.zip");
        var downloaded = await AddDownloaded("kept");
        var library = LibraryReplacing();
        var source = NexusModSources.Create(connection, login: Login(true), downloader: DownloaderReturning((newer, downloaded)), library: library);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([UpdateOf((installed.Current, newer))], Replace: false, MayOpenBrowser: true), CancellationToken.None);

        outcome.Downloaded.Select(item => item.Id).Should().Equal(downloaded.Id);
        outcome.Replacements.Should().BeEmpty();
        await library.DidNotReceiveWithAnyArgs().ReplaceLinkedItemsInAllLoadouts(default, default, default!);
    }

    [Fact]
    public async Task Premium_AFailedSwapIsReportedWithItsResult()
    {
        var installed = await AddInstalled("Unswappable");
        var newer = await AddNewer(installed.Current, "unswappable-newer.zip");
        var downloaded = await AddDownloaded("unswappable");
        var source = NexusModSources.Create(connection, login: Login(true), downloader: DownloaderReturning((newer, downloaded)), library: LibraryReplacing(LibraryItemReplacementResult.FailedUnknownReason));

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([UpdateOf((installed.Current, newer))], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.Replacements.Should().ContainSingle().Which.Result.Should().Be(LibraryItemReplacementResult.FailedUnknownReason);
    }

    [Fact]
    public async Task Premium_AFailedDownloadComesBackNarrowedToThatFile_AndTheOthersStillApply()
    {
        var good = await AddInstalled("Good");
        var bad = await AddInstalled("Bad");
        var goodNewer = await AddNewer(good.Current, "good-newer.zip");
        var badNewer = await AddNewer(bad.Current, "bad-newer.zip");
        var downloaded = await AddDownloaded("good");
        var failure = new HttpRequestException("offline");
        var downloader = Substitute.For<INexusUpdateDownloader>();
        downloader.Download(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<IReadOnlyList<NexusModsFileMetadata.ReadOnly>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<NexusModsFileMetadata.ReadOnly>().Id == badNewer.Id ? ValueTask.FromException<LibraryFile.ReadOnly>(failure) : ValueTask.FromResult(downloaded));
        var source = NexusModSources.Create(connection, login: Login(true), downloader: downloader, library: LibraryReplacing());
        var both = UpdateOf((good.Current, goodNewer), (bad.Current, badNewer));

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([both], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        var failed = outcome.DownloadFailures.Should().ContainSingle().Subject;
        failed.Name.Should().Be("bad-newer.zip");
        failed.Error.Should().BeSameAs(failure);
        var narrowed = failed.Update.Should().BeOfType<NexusModUpdate>().Subject;
        narrowed.Updates.FileMappings.Should().ContainSingle().Which.NewestFile.Id.Should().Be(badNewer.Id);
        narrowed.CurrentItems.Should().Equal(bad.Library.AsLibraryItem().LibraryItemId);
        outcome.Replacements.Should().ContainSingle().Which.Old.Id.Should().Be(good.Library.Id);
    }

    [Fact]
    public async Task Premium_ParallelDownloadsNeverExceedTheConfiguredLimit()
    {
        var installed = new List<Installed>();
        var updates = new List<ModUpdate>();
        var downloader = Substitute.For<INexusUpdateDownloader>();
        var running = 0;
        var peak = 0;
        var downloaded = await AddDownloaded("limit");
        async ValueTask<LibraryFile.ReadOnly> SlowDownload()
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
            return downloaded;
        }
        downloader.Download(Arg.Any<NexusModsFileMetadata.ReadOnly>(), Arg.Any<IReadOnlyList<NexusModsFileMetadata.ReadOnly>>(), Arg.Any<CancellationToken>())
            .Returns(_ => SlowDownload());
        for (var i = 0; i < 5; i++)
        {
            var current = await AddInstalled("Limited" + i);
            installed.Add(current);
            updates.Add(UpdateOf((current.Current, await AddNewer(current.Current, "limit-newer" + i + ".zip"))));
        }
        var source = NexusModSources.Create(connection, login: Login(true), downloader: downloader, library: LibraryReplacing(), maxParallelDownloads: 2);

        await source.ApplyUpdates(new ModUpdateRequest(updates, Replace: false, MayOpenBrowser: true), CancellationToken.None);

        peak.Should().Be(2);
    }

    [Fact]
    public async Task Free_WithMayOpenBrowser_OpensAnNxmLinkPerNewestFile_AndDownloadsNothing()
    {
        var first = await AddInstalled("FreeFirst");
        var second = await AddInstalled("FreeSecond");
        var firstNewer = await AddNewer(first.Current, "free-first-newer.zip");
        var secondNewer = await AddNewer(second.Current, "free-second-newer.zip");
        var downloader = Substitute.For<INexusUpdateDownloader>();
        var os = Substitute.For<IOSInterop>();
        var opened = new ConcurrentQueue<Uri>();
        os.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call => opened.Enqueue(call.Arg<Uri>()));
        var source = NexusModSources.Create(connection, login: Login(false), downloader: downloader, os: os);
        Uri LinkTo(NexusModsFileMetadata.ReadOnly file) => NexusModsUrlBuilder.GetFileDownloadUri(file.ModPage.GameDomain, file.ModPage.Uid.ModId, file.Uid.FileId, useNxmLink: true);

        var outcome = await source.ApplyUpdates(
            new ModUpdateRequest([UpdateOf((first.Current, firstNewer)), UpdateOf((second.Current, secondNewer))], Replace: true, MayOpenBrowser: true),
            CancellationToken.None);

        outcome.OpenedInBrowser.Should().BeEquivalentTo([LinkTo(firstNewer), LinkTo(secondNewer)]);
        opened.Should().BeEquivalentTo(outcome.OpenedInBrowser);
        outcome.AccountCannotDownload.Should().BeFalse();
        outcome.Downloaded.Should().BeEmpty();
        await downloader.DidNotReceiveWithAnyArgs().Download(default, default!, default);
    }

    [Fact]
    public async Task Free_WithoutMayOpenBrowser_OpensNothing_AndSaysTheAccountCannotDownload()
    {
        var installed = await AddInstalled("FreeQuiet");
        var newer = await AddNewer(installed.Current, "free-quiet-newer.zip");
        var downloader = Substitute.For<INexusUpdateDownloader>();
        var os = Substitute.For<IOSInterop>();
        var source = NexusModSources.Create(connection, login: Login(false), downloader: downloader, os: os);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([UpdateOf((installed.Current, newer))], Replace: true, MayOpenBrowser: false), CancellationToken.None);

        outcome.AccountCannotDownload.Should().BeTrue();
        outcome.OpenedInBrowser.Should().BeEmpty();
        os.DidNotReceiveWithAnyArgs().OpenUri(default!);
        await downloader.DidNotReceiveWithAnyArgs().Download(default, default!, default);
    }

    private sealed record OtherUpdate() : ModUpdate([], "1", "2", null);

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
