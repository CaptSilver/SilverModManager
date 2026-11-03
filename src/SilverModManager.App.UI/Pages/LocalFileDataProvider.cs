using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

[UsedImplicitly]
internal class LocalFileDataProvider : ALibraryFileDataProvider<LocalFile.ReadOnly>
{
    public LocalFileDataProvider(IServiceProvider serviceProvider) : this(serviceProvider.GetRequiredService<IConnection>(), new GameNames(serviceProvider.GetServices<IGameData>())) { }

    private LocalFileDataProvider(IConnection connection, GameNames gameNames) : base(connection, gameNames) { }

    protected override LibraryFile.ReadOnly FileOf(LocalFile.ReadOnly model) => model.AsLibraryFile();

    // A Wabbajack modlist is added as a local file too; the Collections tab shows it, and game removal gets it from
    // WabbajackModlistDataProvider, so listing it here as well would delete it twice.
    protected override bool IsListed(LocalFile.ReadOnly model) => !model.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _);

    protected override string SourceOf(LocalFile.ReadOnly model) => ModSources.LocalFile;
}
