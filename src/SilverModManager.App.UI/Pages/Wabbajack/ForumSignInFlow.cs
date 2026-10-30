using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SilverModManager.App.UI.Windows;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Auth;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <inheritdoc cref="IForumSignInFlow" />
[UsedImplicitly]
public sealed class ForumSignInFlow(IForumAccounts accounts, IWindowNotificationService toasts, IErrorPresenter errors, ILogger<ForumSignInFlow> logger) : IForumSignInFlow
{
    /// <inheritdoc />
    public async Task Run(ForumSite site, CancellationToken cancellationToken)
    {
        try
        {
            await accounts.SignIn(site, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is SignInDeclinedException or SignInTimedOutException)
        {
            // The user's own choice or a closed browser tab: worth a line on screen, not a dialog.
            logger.LogInformation("Sign-in to {Site} ended: {Reason}", site, e.Message);
            toasts.ShowToast(e.Message, ToastNotificationVariant.Failure);
        }
        catch (Exception e)
        {
            await errors.Report(e, $"signing in to {site.DisplayName()}");
        }
    }
}
