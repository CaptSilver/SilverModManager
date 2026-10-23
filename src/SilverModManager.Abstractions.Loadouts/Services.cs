using Microsoft.Extensions.DependencyInjection;
using NexusMods.HyperDuck.Adaptor.Impls.ValueAdaptor;
using NexusMods.HyperDuck.BindingConverters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
///     Adds games related serialization services.
/// </summary>
public static class Services
{
    /// <summary>
    ///     Adds known Game entity related serialization services.
    /// </summary>
    public static IServiceCollection AddLoadoutAbstractions(this IServiceCollection services)
    {
        return services
            .AddBindingConverter<LoadoutId, ulong>(l => l.Value.Value)
            .AddBindingConverter<LocationId, ushort>(l => l.Value)
            .AddValueAdaptor<ushort, LocationId>(LocationId.From)
            .AddLoadoutModel()
            .AddLoadoutItemModel()
            .AddLoadoutItemGroupModel()
            .AddLoadoutGameFilesGroupModel()
            .AddLoadoutOverridesGroupModel()
            .AddGameInstalledGroupModel()
            .AddGameInstalledItemModel()
            .AddGameInstalledFileModel()
            .AddGameInstalledClaimModel()
            .AddLibraryLinkedLoadoutItemModel()
            .AddLoadoutItemWithTargetPathModel()
            .AddLoadoutFileModel()
            .AddLoadoutSnapshotModel()
            .AddDeletedFileModel()
            .AddCollectionGroupModel()
            .AddSortOrderModel()
            .AddGameBackedUpFileModel()
            .AddUserToolModel()
            .AddLoadoutQueriesSql();
    }
}
