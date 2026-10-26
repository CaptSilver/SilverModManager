using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Sdk.Errors;
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
        var logger = loggerFactory.CreateLogger(nameof(CleanupVerbs));

        // Step 1: Revert the managed games to their original state
        var db = conn.Db;
        var managedInstallations = Loadout.All(db)
            .Select(loadout => loadout.InstallationInstance)
            .Distinct();

        var refused = false;
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
                logger.LogFailure(ex, "Error reverting {Game}", installation.Game.DisplayName);
                await renderer.Error(ex, "Error reverting {0}", installation.Game.DisplayName);
            }
        }

        // The app's data is what the next attempt needs to revert the game, so keep it until nothing is running.
        if (refused) return 1;

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
            var appFiles = loggingSettings.GetProcessLogFilePaths().Select(path => path.ToPath(fileSystem)).ToArray();

            var appDirectories = new[]
            {
                dataModelSettings.MnemonicDBPath.ToPath(fileSystem),
                fileExtractorSettings.TempFolderLocation.ToPath(fileSystem),
                LoggingSettings.GetLogBaseFolder(fileSystem),

                // The DataModel folder.
                DataModelSettings.GetStandardDataModelFolder(fileSystem),

                // Local Application Data (where all app files default to).
                DataModelSettings.GetLocalApplicationDataDirectory(fileSystem),
            }.Concat(dataModelSettings.ArchiveLocations.Select(path => path.ToPath(fileSystem)));

            await DeleteRemainingFilesUnix(renderer, logger, appFiles, appDirectories);

            await renderer.Text("Application uninstalled successfully");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogFailure(ex, "Error deleting application directories");
            await renderer.Error(ex, "Error deleting application directories");
            return -1;
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static async Task DeleteRemainingFilesUnix(IRenderer renderer, ILogger logger, AbsolutePath[] appFiles, IEnumerable<AbsolutePath> appDirectories)
    {
        await DeleteFilesUnix(renderer, logger, appFiles);
        await DeleteDirectoriesUnix(renderer, logger, appDirectories);
    }
    
    private static async Task DeleteDirectoriesUnix(IRenderer renderer, ILogger logger, IEnumerable<AbsolutePath> appDirectories)
    {
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
            }
        }
    }

    private static async Task DeleteFilesUnix(IRenderer renderer, ILogger logger, AbsolutePath[] appFiles)
    {
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
            }
        }
    }
}
