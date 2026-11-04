using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.App.UI.Controls;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages;

/// <summary>
/// Files a download left in the library with only a <see cref="DownloadedFile"/> to say so: what a Wabbajack list's
/// CDN, MediaFire, ModDB and the other host jobs produce, as well as a plain http download. The Nexus provider lists
/// the Nexus downloads, which carry a DownloadedFile too, and the local file provider lists the ones with a LocalFile.
/// </summary>
[UsedImplicitly]
internal class DownloadedFileDataProvider : ALibraryFileDataProvider<DownloadedFile.ReadOnly>
{
    public DownloadedFileDataProvider(IServiceProvider serviceProvider) : this(serviceProvider.GetRequiredService<IConnection>(), new GameNames(serviceProvider.GetServices<IGameData>()),
        serviceProvider.GetService<ILogger<DownloadedFileDataProvider>>() ?? NullLogger<DownloadedFileDataProvider>.Instance) { }

    private DownloadedFileDataProvider(IConnection connection, GameNames gameNames, ILogger logger) : base(connection, gameNames, logger) { }

    protected override LibraryFile.ReadOnly FileOf(DownloadedFile.ReadOnly model) => model.AsLibraryFile();

    protected override bool IsListed(DownloadedFile.ReadOnly model)
    {
        var item = model.AsLibraryFile().AsLibraryItem();
        // A modlist fetched from the gallery is a download too; WabbajackModlistDataProvider lists it.
        return !item.TryGetAsNexusModsLibraryItem(out _) && !item.TryGetAsLocalFile(out _)
            && !model.AsLibraryFile().TryGetAsWabbajackModlistLibraryFile(out _);
    }

    /// <summary>The site the file was downloaded from, such as "mediafire.com".</summary>
    protected override string SourceOf(DownloadedFile.ReadOnly model)
    {
        var host = model.DownloadPageUri.Host;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host.Length == 0 ? ModSources.Download : host;
    }
}
