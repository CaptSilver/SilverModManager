using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.FileExtractor;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.App.Commandline;

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
        [Injected] ISynchronizerService syncService)
    {
        // Step 1: Revert the managed games to their original state
        var db = conn.Db;
        var managedInstallations = Loadout.All(db)
            .Select(loadout => loadout.InstallationInstance)
            .Distinct();

        foreach (var installation in managedInstallations)
        {
            try
            {
                await syncService.UnManage(installation, false);
                await renderer.Text($"Reverted {installation.Game.DisplayName} to its original state");
            }
            catch (Exception ex)
            {
                await renderer.Error(ex, "Error reverting {0}: {1}", installation.Game.DisplayName, ex.Message);
            }
        }

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
            var appFiles = (AbsolutePath[])
            [
                loggingSettings.MainProcessLogFilePath.ToPath(fileSystem),
                loggingSettings.SlimProcessLogFilePath.ToPath(fileSystem),
                loggingSettings.RunProcessLogFilePath.ToPath(fileSystem),
            ];

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

            await DeleteRemainingFilesUnix(renderer, appFiles, appDirectories);

            await renderer.Text("Application uninstalled successfully");
            return 0;
        }
        catch (Exception ex)
        {
            await renderer.Error(ex, $"Error deleting application directories: {ex.Message}");
            return -1;
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private static async Task DeleteRemainingFilesUnix(IRenderer renderer, AbsolutePath[] appFiles, IEnumerable<AbsolutePath> appDirectories)
    {
        await DeleteFilesUnix(renderer, appFiles);
        await DeleteDirectoriesUnix(renderer, appDirectories);
    }
    
    private static async Task DeleteDirectoriesUnix(IRenderer renderer, IEnumerable<AbsolutePath> appDirectories)
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
                await renderer.Error(e, "Failed to delete directory: {0}", directory); 
            }
        }
    }

    private static async Task DeleteFilesUnix(IRenderer renderer, AbsolutePath[] appFiles)
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
                await renderer.Error(e, "Failed to delete file: {0}", appFile); 
            }
        }
    }
}
