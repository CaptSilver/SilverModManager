using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.UI.Sdk.Icons;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.LoadoutPage;

[JsonName("SilverModManager.App.UI.Pages.Library.CollectionLoadoutPageContext")]
public record CollectionLoadoutPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }

    public required CollectionGroupId GroupId { get; init; }

    public Optional<CollectionRevisionMetadataId> RevisionId { get; init; }
}

[UsedImplicitly]
public class CollectionLoadoutPageFactory : APageFactory<ICollectionLoadoutViewModel, CollectionLoadoutPageContext>
{
    private readonly IConnection _connection;
    private readonly CollectionProviders _providers;

    public CollectionLoadoutPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _providers = serviceProvider.GetRequiredService<CollectionProviders>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("cd8b1f11-1d6d-4430-bcbf-33b6001ee743"));
    public override PageFactoryId Id => StaticId;

    public override ICollectionLoadoutViewModel CreateViewModel(CollectionLoadoutPageContext context)
    {
        var group = CollectionGroup.Load(_connection.Db, context.GroupId);
        if (group.IsValid() is false)
            throw new PageTargetGoneException($"Collection {context.GroupId} no longer exists");

        // RevisionId stays in the context for saved workspaces; the source reads the group's revision itself.
        return new CollectionLoadoutViewModel(
            ServiceProvider.GetRequiredService<IWindowManager>(),
            ServiceProvider,
            _providers.For(group, context.LoadoutId));
    }
}
