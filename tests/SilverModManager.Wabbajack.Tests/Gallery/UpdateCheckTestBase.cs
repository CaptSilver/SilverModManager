using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Gallery;

/// <summary>A gallery client that serves a fixed cached listing and fails any call that would reach the network.</summary>
internal sealed class CachedOnlyGallery : IWabbajackGalleryClient
{
    public Subject<GalleryListing> RefreshedSubject { get; } = new();
    public GalleryListing? Cached { get; set; }
    public Exception? GetFailure { get; set; }
    public int GetCalls { get; private set; }

    public IObservable<GalleryListing> Refreshed => RefreshedSubject;

    public GalleryListing? GetCached() => Cached;

    public Task<GalleryListing> Get(bool refresh = false, CancellationToken cancellationToken = default)
    {
        GetCalls++;
        if (GetFailure is { } failure) throw failure;
        return Cached is { } listing ? Task.FromResult(listing) : throw new InvalidOperationException("The test gave the gallery no listing.");
    }
}

/// <summary>An installed-modlist fixture with a doubled gallery in place of the network.</summary>
public abstract class UpdateCheckTestBase<TTest>(ITestOutputHelper helper) : InstallTestBase<TTest>(helper)
    where TTest : class
{
    internal CachedOnlyGallery Gallery { get; } = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        return services.AddSingleton<IWabbajackGalleryClient>(Gallery);
    }

    /// <summary>A group installed from a new library list, recording the identity a gallery download writes.</summary>
    protected async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddInstalled(Loadout.ReadOnly loadout, string name, string? machineUrl = "a-list",
        string repository = "wabbajack-tools/mod-lists", DateTimeOffset? dateUpdated = null)
    {
        var file = await AddModlist(new ListBuilder().ToList(name), new Dictionary<string, byte[]>());
        if (machineUrl is not null) file = await WithIdentity(Connection, file, machineUrl, repository, dateUpdated);

        using var tx = Connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = file.Id,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
                },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }
}
