using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileExtractor;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.App.Commandline;

/// <summary>
///     These verbs are used in cleaning up unneeded data related to the application.
///     Namely, Garbage Collection tasks.
/// </summary>
internal static class CleanupVerbs
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    internal static IServiceCollection AddCleanupVerbs(this IServiceCollection services) =>
        services.AddVerb(() => UninstallApp);
    
    [Verb("uninstall-app", "Uninstall the application and revert games to their original state")]
    private static async Task<int> UninstallApp(
        [Injected] IRenderer renderer,
        [Injected] IConnection conn,
        [Injected] ISettingsManager settingsManager,
        [Injected] IFileSystem fileSystem,
        [Injected] ISynchronizerService syncService,
        [Injected] ILoggerFactory loggerFactory)
    {
        var managedInstallations = Loadout.All(conn.Db)
            .Select(loadout => loadout.InstallationInstance)
            .Distinct()
            .ToArray();

        var exitCode = await Uninstall(renderer, loggerFactory.CreateLogger(nameof(CleanupVerbs)), settingsManager, fileSystem, syncService, managedInstallations);

        // The database this process holds is gone, and shutting down normally would write to it and recreate the
        // folders just deleted, so the process ends here with the uninstall's own exit code.
        Environment.Exit(exitCode);
        return exitCode;
    }

    /// <summary>
    /// Reverts the managed games and deletes the application's data. Returns 0 on success, 1 when a game is still
    /// running, and -1 when something could not be reverted or deleted. The logs go last and only when everything
    /// else went, so a failure's pointer to the log folder still leads somewhere.
    /// </summary>
    internal static async Task<int> Uninstall(
        IRenderer renderer,
        ILogger logger,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        ISynchronizerService syncService,
        IEnumerable<GameInstallation> managedInstallations)
    {
        // Step 1: Revert the managed games to their original state
        var refused = false;
        var failed = false;
        foreach (var installation in managedInstallations)
        {
            try
            {
                await syncService.UnManage(installation, false);
                await renderer.Text($"Reverted {installation.Game.DisplayName} to its original state");
            }
            catch (ExecutableInUseException ex)
            {
                refused = true;
                await renderer.Text("{0}", ex.Message);
            }
            catch (Exception ex)
            {
                failed = true;
                logger.LogFailure(ex, "Error reverting {Game}", installation.Game.DisplayName);
                await renderer.Error(ex, "Error reverting {0}", installation.Game.DisplayName);
            }
        }

        // The app's data is what the next attempt needs to revert the game, so keep it until nothing is running.
        if (refused) return CliErrors.RefusalExitCode;

        // Step 2: Delete application-specific directories
        try
        {
            /*
                Note (Sewer): There's a possibility some data may be left behind if
                the user manually modified the logging configuration to point
                their logs outside of the regular 'Logs' folder.

                Specifically, the historical logs may use Layout Renderers
                https://nlog-project.org/config/?tab=layout-renderers that
                are other than {##}. Resolving these on our end would be hard.
            */
            var dataModelSettings = settingsManager.Get<DataModelSettings>();
            var fileExtractorSettings = settingsManager.Get<FileExtractorSettings>();
            var loggingSettings = settingsManager.Get<LoggingSettings>();
            var logFiles = loggingSettings.GetProcessLogFilePaths().Select(path => path.ToPath(fileSystem)).ToArray();

            var appDirectories = new[]
            {
                dataModelSettings.MnemonicDBPath.ToPath(fileSystem),
                fileExtractorSettings.TempFolderLocation.ToPath(fileSystem),

                // The DataModel folder.
                DataModelSettings.GetStandardDataModelFolder(fileSystem),

                // Local Application Data (where all app files default to).
                DataModelSettings.GetLocalApplicationDataDirectory(fileSystem),
            }.Concat(dataModelSettings.ArchiveLocations.Select(path => path.ToPath(fileSystem)));

            failed |= !await DeleteDirectoriesUnix(renderer, logger, appDirectories);

            if (!failed)
            {
                failed |= !await DeleteFilesUnix(renderer, logger, logFiles);
                failed |= !await DeleteDirectoriesUnix(renderer, logger, [LoggingSettings.GetLogBaseFolder(fileSystem)]);
            }

            if (failed)
            {
                await renderer.Text("The application was not fully uninstalled. {0}", CliErrors.CrashPointer);
                return CliErrors.CrashExitCode;
            }

            await renderer.Text("Application uninstalled successfully");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogFailure(ex, "Error deleting application directories");
            await renderer.Error(ex, "Error deleting application directories");
            return CliErrors.ExitCode(ex);
        }
    }

    /// <returns>False when any directory could not be deleted.</returns>
    private static async Task<bool> DeleteDirectoriesUnix(IRenderer renderer, ILogger logger, IEnumerable<AbsolutePath> appDirectories)
    {
        var allDeleted = true;
        foreach (var directory in appDirectories)
        {
            if (!directory.DirectoryExists())
                continue;

            try
            {
                directory.DeleteDirectory(recursive: true);
                await renderer.Text("Deleted directory: {0}", directory);
            }
            catch (Exception e)
            {
                logger.LogFailure(e, "Failed to delete directory {Directory}", directory);
                await renderer.Error(e, "Failed to delete directory: {0}", directory);
                allDeleted = false;
            }
        }

        return allDeleted;
    }

    /// <returns>False when any file could not be deleted.</returns>
    private static async Task<bool> DeleteFilesUnix(IRenderer renderer, ILogger logger, AbsolutePath[] appFiles)
    {
        var allDeleted = true;
        foreach (var appFile in appFiles)
        {
            if (!appFile.FileExists)
                continue;

            try
            {
                appFile.Delete();
                await renderer.Text("Deleted file: {0}", appFile);
            }
            catch (Exception e)
            {
                logger.LogFailure(e, "Failed to delete file {File}", appFile);
                await renderer.Error(e, "Failed to delete file: {0}", appFile);
                allDeleted = false;
            }
        }

        return allDeleted;
    }
}
