using Microsoft.Extensions.Logging;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.GameBanana;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Types.IpcHandlers;

/// <summary>
/// A handler for the <c>silvermodmanager://mod/install/{modId}/{fileId}</c> one-click install links GameBanana builds.
/// The link names a mod and a file but no game, so the handler asks GameBanana which game the mod is listed under and
/// picks the managed game by that.
/// </summary>
public class GameBananaIpcProtocolHandler(
    ILogger<GameBananaIpcProtocolHandler> logger,
    IEventBus eventBus,
    IGameBananaInstaller installer,
    ManagedGames managedGames) : IIpcProtocolHandler
{
    /// <inheritdoc/>
    public string Protocol => GameBananaUrl.Scheme;

    /// <inheritdoc/>
    public bool IsRegistrationWanted(ISettingsManager settings) => settings.Get<GameBananaHandlerSettings>().RegisterAsDefaultHandler;

    /// <inheritdoc/>
    public Observable<Unit> RegistrationTurnedOn(ISettingsManager settings)
        => settings.GetChanges<GameBananaHandlerSettings>().Where(static changed => changed.RegisterAsDefaultHandler).Select(static _ => Unit.Default);

    /// <inheritdoc/>
    public async Task Handle(string url, CancellationToken token)
    {
        logger.LogDebug("Received GameBanana URL: {Url}", url);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) is false)
        {
            Refuse($"Cannot open '{url}': it is not a link.");
            return;
        }

        if (GameBananaUrl.TryParse(uri, out var parsed, out var problem) is false)
        {
            Refuse(problem);
            return;
        }

        try
        {
            if (await installer.GameOf(parsed.ModId, token) is not { } gameBananaId)
            {
                Refuse($"GameBanana has no mod {parsed.ModId} that can be installed. It may be private or deleted.");
                return;
            }

            var loadout = LinkTarget.Choose(
                managedGames,
                game => GameBananaGame.For(game)?.GameBananaGameId == gameBananaId,
                linked: $"GameBanana mod {parsed.ModId}",
                wanted: $"GameBanana game {gameBananaId}",
                eventBus, logger);
            if (loadout is not { } chosen) return;

            eventBus.Send(new CliMessages.ModDownloadStarted());

            switch (await installer.Install(parsed.ModId, parsed.FileId, chosen.Id, token))
            {
                case CatalogInstallOutcome.Installed { PerPackage: [var root, ..] }:
                    eventBus.Send(new CliMessages.ModDownloadSucceeded(root.Item));
                    eventBus.Send(new CliMessages.ModInstalled(root.Item, chosen.Name));
                    break;
                case CatalogInstallOutcome.Refused refused:
                    logger.LogWarning(refused.Why, "Installing GameBanana mod {ModId} was refused", parsed.ModId);
                    // The toast shows the message as plain text.
                    Refuse(MarkdownText.ToPlain(refused.Why.Message));
                    break;
                default:
                    Refuse($"GameBanana mod {parsed.ModId} was not installed.");
                    break;
            }
        }
        catch (UserFacingException e)
        {
            logger.LogWarning(e, "Installing GameBanana mod {ModId} was refused", parsed.ModId);
            Refuse(MarkdownText.ToPlain(e.Message));
        }
        catch (Exception e)
        {
            eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Unknown(e)));
            throw;
        }
    }

    private void Refuse(string message)
    {
        logger.LogWarning("{Message}", message);
        eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Refused(message)));
    }
}
