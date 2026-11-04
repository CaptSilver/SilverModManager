using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi.Errors;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.Tests;

/// <summary>
/// The mod page's uid is indexed but not unique, so two callers resolving one mod at once must not each add a row.
/// </summary>
public class ModPageResolutionTests(IServiceProvider serviceProvider)
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(1704);

    private sealed class OverridingServiceProvider(IServiceProvider inner, params (Type Type, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            foreach (var (type, instance) in overrides)
                if (type == serviceType) return instance;
            return inner.GetService(serviceType);
        }
    }

    /// <summary>
    /// A client that answers for the mod after a delay, so every caller is past its "is the page there yet" check
    /// before any of them has written.
    /// </summary>
    private static IGraphQlClient SlowClient(ModUid uid, TimeSpan delay)
    {
        // The code generator names the type of IMod.Game after whichever operation it meets first, so the name changes
        // from one generation to the next; take the type from the property instead of spelling it.
        var gameProperty = typeof(IMod).GetProperty(nameof(IMod.Game))!;
        var domain = Substitute.For([gameProperty.PropertyType], []);
        gameProperty.PropertyType.GetProperty("DomainName")!.GetValue(domain).Returns("skyrimspecialedition");
        var mod = Substitute.For<IMod>();
        mod.Uid.Returns(uid.ToV2Api());
        mod.Name.Returns("A mod");
        gameProperty.GetValue(mod).Returns(domain);
        mod.UpdatedAt.Returns(DateTimeOffset.UnixEpoch);

        IModFile[] noFiles = [];
        var client = Substitute.For<IGraphQlClient>();
        client.QueryMod(uid.ModId, uid.GameId, Arg.Any<CancellationToken>())
            .Returns(_ => AfterDelay(delay, new GraphQlResult<IMod, NotFound>(mod)));
        client.QueryModFiles(uid.ModId, uid.GameId, Arg.Any<CancellationToken>())
            .Returns(_ => AfterDelay(delay, new GraphQlResult<IModFile[], NotFound>(noFiles)));
        return client;
    }

    private static async ValueTask<T> AfterDelay<T>(TimeSpan delay, T result)
    {
        await Task.Delay(delay);
        return result;
    }

    [Fact]
    public async Task ConcurrentResolutionsOfOneMod_ShareOnePage()
    {
        var uid = new ModUid(ModId.From(424242), Game);
        var library = new NexusModsLibrary(new OverridingServiceProvider(serviceProvider, (typeof(IGraphQlClient), SlowClient(uid, TimeSpan.FromMilliseconds(150)))));
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var pages = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => await library.GetOrAddModPage(uid.ModId, uid.GameId))));

        NexusModsModPageMetadata.FindByUid(connection.Db, uid).Should().HaveCount(1);
        pages.Select(p => p.Id).Distinct().Should().HaveCount(1, "every caller is handed the page that was written");
    }

    [Fact]
    public async Task ConcurrentResolutionsOfOneFile_ShareOneRow()
    {
        var uid = new ModUid(ModId.From(424243), Game);
        var fileId = FileId.From(9001);
        var fileUid = new FileUid(fileId, Game);
        var client = SlowClient(uid, TimeSpan.Zero);

        var modFile = Substitute.For<IModFile>();
        modFile.Name.Returns("A file");
        modFile.Version.Returns("1");
        modFile.SizeInBytes.Returns("10");
        modFile.Date.Returns(0);
        client.QueryModFile(fileId, Game, Arg.Any<CancellationToken>())
            .Returns(_ => AfterDelay(TimeSpan.FromMilliseconds(150), new GraphQlResult<IModFile, NotFound>(modFile)));

        var library = new NexusModsLibrary(new OverridingServiceProvider(serviceProvider, (typeof(IGraphQlClient), client)));
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var page = await library.GetOrAddModPage(uid.ModId, uid.GameId);

        var files = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () => await library.GetOrAddFile(fileId, page))));

        NexusModsFileMetadata.FindByUid(connection.Db, fileUid).Should().HaveCount(1);
        files.Select(f => f.Id).Distinct().Should().HaveCount(1, "every caller is handed the file that was written");
    }
}
