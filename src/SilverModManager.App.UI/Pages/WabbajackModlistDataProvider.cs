using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Finds the Wabbajack modlists in the library for game removal. They have no Mods tab row: they are cards on the
/// Collections tab, and <see cref="LocalFileDataProvider"/> leaves them out too.
/// </summary>
[UsedImplicitly]
internal class WabbajackModlistDataProvider : ALibraryDataProvider<WabbajackModlistLibraryFile.ReadOnly>
{
    public WabbajackModlistDataProvider(IServiceProvider serviceProvider) : base(serviceProvider.GetRequiredService<IConnection>()) { }

    protected override Optional<LibraryFile.ReadOnly> AsLibraryFile(WabbajackModlistLibraryFile.ReadOnly model) => model.AsLibraryFile();

    // Modlists are on the Collections tab, so the Mods tab has no row for them. The provider stays registered because
    // removing a game still asks it for the lists to delete.
    public override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> ObserveLibraryItems(LibraryFilter libraryFilter)
        => Observable.Never<IChangeSet<CompositeItemModel<EntityId>, EntityId>>().StartWith(ChangeSet<CompositeItemModel<EntityId>, EntityId>.Empty);
}
