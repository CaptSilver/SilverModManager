using Avalonia.Platform.Storage;
using Humanizer;
using Humanizer.Bytes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Extensions;
using SilverModManager.App.UI.Overlays;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using R3;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.CollectionDownload;

public interface IManualDownloadRequiredOverlayViewModel : IOverlayViewModel
{
    string DownloadName { get; }
    string ExpectedHash { get; }
    string ExpectedSize { get; }

    bool HasInstructions { get; }
    string Instructions { get; }

    /// <summary>
    /// Whether the download page is a tab of a collection page, which keeps the collection's instructions on its own tab.
    /// </summary>
    bool IsEmbedded { get; }

    bool IsCheckingFile { get; }
    bool IsIncorrectFile { get; }
    string ReceivedHash { get; }

    ReactiveCommand CommandCancel { get; }
    ReactiveCommand CommandOpenBrowser { get; }
    ReactiveCommand CommandAddFile { get; }
    ReactiveCommand CommandTryAgain { get; }
    ReactiveCommand CommandReportBug { get; }
}

public class ManualDownloadRequiredOverlayDesignViewModel : AOverlayViewModel<IManualDownloadRequiredOverlayViewModel>, IManualDownloadRequiredOverlayViewModel
{
    public ManualDownloadRequiredOverlayDesignViewModel() { }

    public ManualDownloadRequiredOverlayDesignViewModel(bool isEmbedded, bool isIncorrectFile)
    {
        IsEmbedded = isEmbedded;
        IsCheckingFile = false;
        IsIncorrectFile = isIncorrectFile;
    }

    public string DownloadName => "Mod Name";
    public string ExpectedHash => "123abc";
    public string ExpectedSize => "112 MB";
    public bool HasInstructions => true;
    public string Instructions => "Click the 3rd link down under the heading “Latest downloads”. Ignore the advert above it. Make sure the file size matches 112mb.";
    public bool IsEmbedded { get; }
    public bool IsCheckingFile { get; } = true;
    public bool IsIncorrectFile { get; }

    public string ReceivedHash => "456def";
    public ReactiveCommand CommandCancel { get; } = new();
    public ReactiveCommand CommandOpenBrowser { get; } = new();
    public ReactiveCommand CommandAddFile { get; } = new();
    public ReactiveCommand CommandTryAgain { get; } = new();
    public ReactiveCommand CommandReportBug { get; } = new();
}

public partial class ManualDownloadRequiredOverlayViewModel : AOverlayViewModel<IManualDownloadRequiredOverlayViewModel>, IManualDownloadRequiredOverlayViewModel
{
    public ManualDownloadRequiredOverlayViewModel(IServiceProvider serviceProvider, CollectionDownloadExternal.ReadOnly downloadEntity, bool isEmbedded)
    {
        IsEmbedded = isEmbedded;

        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        var avaloniaInterop = serviceProvider.GetRequiredService<IAvaloniaInterop>();
        var libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var logger = serviceProvider.GetRequiredService<ILogger<ManualDownloadRequiredOverlayViewModel>>();
        var mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();

        DownloadName = downloadEntity.AsCollectionDownload().Name;
        ExpectedHash = downloadEntity.Md5.ToString();
        ExpectedSize = ByteSize.FromBytes(downloadEntity.Size.Value).Humanize();

        HasInstructions = downloadEntity.AsCollectionDownload().Instructions.HasValue;
        Instructions = downloadEntity.AsCollectionDownload().Instructions.ValueOr(string.Empty);

        var gameDomain = mappingCache[downloadEntity.AsCollectionDownload().CollectionRevision.Collection.GameId];
        var revision = downloadEntity.AsCollectionDownload().CollectionRevision;
        var revisionBugsUri = NexusModsUrlBuilder.GetCollectionBugsUri(gameDomain, revision.Collection.Slug, revision.RevisionNumber);

        CommandCancel = new ReactiveCommand(_ => { base.Close(); });
        CommandOpenBrowser = new ReactiveCommand(execute: _ => osInterop.OpenUri(downloadEntity.Uri));

        CommandAddFile = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var paths = await avaloniaInterop.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = $"Browse file for download \"{downloadEntity.AsCollectionDownload().Name}\"",
                    AllowMultiple = false,
                });

                if (!paths.TryGetFirst(out var file)) return;

                IsCheckingFile = true;

                var localFile = await libraryService.AddLocalFile(file);
                var receivedHash = localFile.AsLibraryFile().Md5.Value;
                ReceivedHash = receivedHash.ToString();

                if (receivedHash == downloadEntity.Md5)
                {
                    logger.LogInformation("Received file with matching hash for download `{DownloadName}` (index={Index})", downloadEntity.AsCollectionDownload().Name, downloadEntity.AsCollectionDownload().ArrayIndex);
                    base.Close();
                    return;
                }

                logger.LogWarning("Received file with hash `{ActualHash}` that doesn't match expected hash of `{ExpectedHash}` for download `{DownloadName}` (index={Index})", receivedHash, downloadEntity.Md5, downloadEntity.AsCollectionDownload().Name, downloadEntity.AsCollectionDownload().ArrayIndex);

                IsCheckingFile = false;
                IsIncorrectFile = true;

                {
                    using var tx = connection.BeginTransaction();
                    tx.Delete(localFile, recursive: true);
                    await tx.Commit();
                }
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandTryAgain = new ReactiveCommand(_ =>
        {
            // reset
            IsCheckingFile = false;
            IsIncorrectFile = false;
            ReceivedHash = string.Empty;
        });

        CommandReportBug = new ReactiveCommand(execute: _ => osInterop.OpenUri(revisionBugsUri));
    }

    public string DownloadName { get; }
    public string ExpectedHash { get; }
    public string ExpectedSize { get; }

    public bool HasInstructions { get; }
    public string Instructions { get; }
    public bool IsEmbedded { get; }

    [Reactive] public partial bool IsCheckingFile { get; private set; }
    [Reactive] public partial bool IsIncorrectFile { get; private set; }
    [Reactive] public partial string ReceivedHash { get; private set; } = string.Empty;

    public ReactiveCommand CommandCancel { get; }
    public ReactiveCommand CommandOpenBrowser { get; }
    public ReactiveCommand CommandAddFile { get; }
    public ReactiveCommand CommandTryAgain { get; }
    public ReactiveCommand CommandReportBug { get; }
}
