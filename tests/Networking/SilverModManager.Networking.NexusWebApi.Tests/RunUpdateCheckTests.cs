using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.ModUpdates;
using SilverModManager.Networking.ModUpdates.Mixins;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// Refreshing the pages the update feed flagged: a cancelled refresh must not be committed as a finished one, and a
/// page Nexus no longer serves must not take the other pages down with it.
/// </summary>
public class RunUpdateCheckTests(IServiceProvider serviceProvider)
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(1704);

    private sealed class OverridingServiceProvider(IServiceProvider inner, IGraphQlClient client) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IGraphQlClient) ? client : inner.GetService(serviceType);
    }

    private static IMod ModFor(ModUid uid)
    {
        // The code generator names the type of IMod.Game after whichever operation it meets first, so take it from the property.
        var gameProperty = typeof(IMod).GetProperty(nameof(IMod.Game))!;
        var domain = Substitute.For([gameProperty.PropertyType], []);
        gameProperty.PropertyType.GetProperty("DomainName")!.GetValue(domain).Returns("skyrimspecialedition");
        var mod = Substitute.For<IMod>();
        mod.Uid.Returns(uid.ToV2Api());
        mod.Name.Returns("A mod");
        gameProperty.GetValue(mod).Returns(domain);
        mod.UpdatedAt.Returns(DateTimeOffset.UnixEpoch);
        return mod;
    }

    private static GraphQlResult<IMod, NotFound> ModFound(ModUid uid) => new(ModFor(uid));
    private static GraphQlResult<IModFile[], NotFound> NoFiles() => new(Array.Empty<IModFile>());
    private static GraphQlResult<T, NotFound> Gone<T>() where T : notnull
        => new(new NotFound { Message = "removed" });

    private async Task<PageMetadataMixin> AddPage(ModUid uid)
    {
        var client = Substitute.For<IGraphQlClient>();
        client.QueryMod(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ModFound(uid)));
        client.QueryModFiles(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(NoFiles()));
        var library = new NexusModsLibrary(new OverridingServiceProvider(serviceProvider, client));
        await library.GetOrAddModPage(uid.ModId, uid.GameId);

        var db = serviceProvider.GetRequiredService<IConnection>().Db;
        return PageMetadataMixin.EnumerateDatabaseEntries(db).Single(p => p.GetModPageId() == uid);
    }

    private static PerFeedCacheUpdaterResult<PageMetadataMixin> OutOfDate(params PageMetadataMixin[] pages)
        => new() { OutOfDateItems = [.. pages] };

    // Adding a page stamps it once already, so a refresh shows as a later stamp.
    private DateTimeOffset StampOf(ModUid uid)
    {
        var db = serviceProvider.GetRequiredService<IConnection>().Db;
        var page = NexusModsModPageMetadata.FindByUid(db, uid).Single();
        return NexusModsModPageMetadata.DataUpdatedAt.Get(page);
    }

    [Fact]
    public async Task CancelledRefresh_Throws_SoTheCallerDoesNotCommitIt()
    {
        var uid = new ModUid(ModId.From(515151), Game);
        var page = await AddPage(uid);
        using var cts = new CancellationTokenSource();

        var client = Substitute.For<IGraphQlClient>();
        client.QueryMod(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ModFound(uid)));
        // The cancel lands between the mod query and the file query.
        client.QueryModFiles(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns<ValueTask<GraphQlResult<IModFile[], NotFound>>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var connection = serviceProvider.GetRequiredService<IConnection>();
        using var tx = connection.BeginTransaction();
        var act = async () => await RunUpdateCheck.UpdateModFilesForOutdatedPages(connection.Db, tx, NullLogger.Instance, client, OutOfDate(page), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OutOfDatePageNexusNoLongerServes_IsSkipped_AndTheOtherPagesAreStillRefreshed()
    {
        var gone = new ModUid(ModId.From(515152), Game);
        var kept = new ModUid(ModId.From(515153), Game);
        var gonePage = await AddPage(gone);
        var keptPage = await AddPage(kept);
        var keptBefore = StampOf(kept);
        var goneBefore = StampOf(gone);

        var client = Substitute.For<IGraphQlClient>();
        client.QueryMod(gone.ModId, gone.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Gone<IMod>()));
        client.QueryModFiles(gone.ModId, gone.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Gone<IModFile[]>()));
        client.QueryMod(kept.ModId, kept.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ModFound(kept)));
        client.QueryModFiles(kept.ModId, kept.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(NoFiles()));

        var connection = serviceProvider.GetRequiredService<IConnection>();
        using (var tx = connection.BeginTransaction())
        {
            await RunUpdateCheck.UpdateModFilesForOutdatedPages(connection.Db, tx, NullLogger.Instance, client, OutOfDate(gonePage, keptPage), CancellationToken.None);
            await tx.Commit();
        }

        StampOf(kept).Should().BeAfter(keptBefore);
        StampOf(gone).Should().Be(goneBefore,"a page that could not be fetched must not be stamped as refreshed");
    }

    [Fact]
    public async Task PageWhoseFilesCannotBeFetched_IsNotStampedAsRefreshed()
    {
        var uid = new ModUid(ModId.From(515154), Game);
        var page = await AddPage(uid);
        var before = StampOf(uid);

        var client = Substitute.For<IGraphQlClient>();
        client.QueryMod(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ModFound(uid)));
        client.QueryModFiles(uid.ModId, uid.GameId, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Gone<IModFile[]>()));

        var connection = serviceProvider.GetRequiredService<IConnection>();
        using (var tx = connection.BeginTransaction())
        {
            await RunUpdateCheck.UpdateModFilesForOutdatedPages(connection.Db, tx, NullLogger.Instance, client, OutOfDate(page), CancellationToken.None);
            await tx.Commit();
        }

        StampOf(uid).Should().Be(before, "the file list was never refreshed, so the page is still due");
    }
}
