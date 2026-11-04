using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using R3;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.CLI.Types.IpcHandlers;

/// <summary>
/// A handler for the <c>ror2mm://</c> "Install with App" links on Thunderstore. The link names a package version but
/// no game, so the handler picks the managed game by the communities the package is listed under.
/// </summary>
public class Ror2mmIpcProtocolHandler(
    ILogger<Ror2mmIpcProtocolHandler> logger,
    IEventBus eventBus,
    IThunderstoreInstaller installer,
    ManagedGames managedGames) : IIpcProtocolHandler
{
    /// <inheritdoc/>
    public string Protocol => Ror2mmUrl.Scheme;

    /// <inheritdoc/>
    public bool IsRegistrationWanted(ISettingsManager settings) => settings.Get<Ror2mmHandlerSettings>().RegisterAsDefaultHandler;

    /// <inheritdoc/>
    public Observable<Unit> RegistrationTurnedOn(ISettingsManager settings)
        => settings.GetChanges<Ror2mmHandlerSettings>().Where(static changed => changed.RegisterAsDefaultHandler).Select(static _ => Unit.Default);

    /// <inheritdoc/>
    public async Task Handle(string url, CancellationToken token)
    {
        logger.LogDebug("Received ror2mm URL: {Url}", url);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) is false)
        {
            Refuse($"Cannot open '{url}': it is not a link.");
            return;
        }

        if (Ror2mmUrl.TryParse(uri, out var parsed, out var problem) is false)
        {
            Refuse(problem);
            return;
        }

        var package = parsed.Version.Package;
        var communities = await installer.CommunitiesOf(package, token);
        if (communities.Count == 0)
        {
            Refuse($"Thunderstore has no package {package}.");
            return;
        }

        var candidates = managedGames.InstallationCandidates(game => ThunderstoreGame.For(game) is { } thunderstore && communities.Contains(thunderstore.ThunderstoreCommunity));
        var inUse = candidates.Count > 1 ? candidates.Where(candidate => candidate.HasAppliedLoadout).ToList() : candidates;
        if (candidates.Count == 0)
        {
            logger.LogWarning("Mod download aborted: no managed game takes mods from {Communities}", communities);
            eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.GameNotManaged(string.Join(", ", communities))));
            return;
        }

        // Several games list the package: the one the user has applied a loadout to is the one in use.
        if (inUse.Count != 1)
        {
            var games = candidates.Select(candidate => candidate.Installation.Game.DisplayName).Distinct().ToList();
            eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Ambiguous(package.ToString(), games)));
            return;
        }

        var loadout = managedGames.LoadoutOf(inUse[0].Installation);
        eventBus.Send(new CliMessages.ModDownloadStarted());

        try
        {
            // The pinned version from the link: that is the one the user clicked.
            var outcomes = await installer.InstallAsync(parsed.Version, loadout.Id, token);
            var root = outcomes.First(outcome => outcome.Version == parsed.Version);
            eventBus.Send(new CliMessages.ModDownloadSucceeded(root.Item));
            eventBus.Send(new CliMessages.ModInstalled(root.Item, loadout.Name));
        }
        // Dependency, integrity and disabled-group failures are deliberate refusals whose message is the content.
        catch (UserFacingException e)
        {
            logger.LogWarning(e, "Installing {Version} was refused", parsed.Version);
            // The toast shows the message as plain text.
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
