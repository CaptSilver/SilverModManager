using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using SilverModManager.CrossPlatform;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Platform.Proton.Support;

/// <summary>
/// Writes a zip under the app's state directory with the log files, the doctor report, the launch state files
/// and each Steam game's launch options.
/// </summary>
/// <remarks>
/// The entries are listed, not discovered: the sync file, the database and the settings files are never added,
/// because they can hold credentials. The one place secrets are expected is an environment prefix in a launch
/// options line, so those values are cut to <c>KEY=...</c> in both the doctor report and the launch options file.
/// </remarks>
public sealed class SupportBundleWriter(
    IFileSystem fileSystem,
    ISettingsManager settingsManager,
    IEnvironmentReader environment,
    DoctorReporter reporter,
    IManagedInstallations installations) : ISupportBundleWriter
{
    private const string DirectoryName = "SupportBundles";

    public async Task<AbsolutePath> WriteAsync(CancellationToken cancellationToken = default)
    {
        // The reporter probes launch locks and reads localconfig.vdf, so keep it off the UI thread.
        var report = await Task.Run(() => Redact(reporter.Build(installations.List())), cancellationToken);
        var logging = settingsManager.Get<LoggingSettings>();

        var directory = LoggingSettings.GetLogBaseFolder(fileSystem).Parent.Combine(DirectoryName);
        fileSystem.CreateDirectory(directory);
        var target = NewTarget(directory);

        await using (var stream = target.Create())
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var log in new[] { logging.MainProcessLogFilePath, logging.SlimProcessLogFilePath, logging.RunProcessLogFilePath })
                await AddLog(zip, log.ToPath(fileSystem), cancellationToken);

            AddText(zip, "doctor.json", report.ToJson());
            AddText(zip, "steam-launch-options.txt", LaunchOptionsText(report));
            AddText(zip, "system.txt", SystemText());
            AddLaunchState(zip);
        }

        return target;
    }

    private static DoctorReport Redact(DoctorReport report) => report with
    {
        Installations = report.Installations
            .Select(i => i.LaunchOptions is { Line: { } line }
                ? i with { LaunchOptions = i.LaunchOptions with { Line = SteamLaunchOptionLine.RedactEnvPrefixValues(line) } }
                : i)
            .ToArray(),
    };

    /// <summary>
    /// A file name that does not exist yet. Bundles are never replaced: the one from before a retry may be what
    /// the user already attached somewhere.
    /// </summary>
    private static AbsolutePath NewTarget(AbsolutePath directory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = directory.Combine($"support-bundle-{stamp}.zip");
        for (var n = 2; target.FileExists; n++) target = directory.Combine($"support-bundle-{stamp}-{n}.zip");
        return target;
    }

    private static string LaunchOptionsText(DoctorReport report)
    {
        var sb = new StringBuilder();
        foreach (var installation in report.Installations.Where(i => i.LaunchOptions is not null))
        {
            var options = installation.LaunchOptions!;
            sb.Append(installation.Game).Append(" (").Append(installation.StoreId).Append("): ")
                .AppendLine(options.Line ?? $"<{options.Status}>");
        }

        return sb.Length == 0 ? "No Steam game with a readable localconfig.vdf.\n" : sb.ToString();
    }

    private static string SystemText() =>
        $"{ApplicationConstants.ProductName} {ApplicationConstants.Version}\n{RuntimeInformation.OSDescription}\n";

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static async Task AddLog(ZipArchive zip, AbsolutePath log, CancellationToken cancellationToken)
    {
        if (!log.FileExists) return;

        // The log is still being written to by the running process.
        await using var source = log.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var entry = zip.CreateEntry($"logs/{log.FileName}").Open();
        await source.CopyToAsync(entry, cancellationToken);
    }

    private void AddLaunchState(ZipArchive zip)
    {
        var runtimeDir = environment.Get("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir)) return;

        var directory = InstallationLock.DirectoryFor(runtimeDir);
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "launch-*.state").Order(StringComparer.Ordinal))
        {
            FileStream source;
            try
            {
                source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                // The session deletes its state file on release, so a game that exits mid-collection leaves nothing to add.
                continue;
            }

            using var _ = source;
            using var entry = zip.CreateEntry($"launch-state/{Path.GetFileName(file)}").Open();
            source.CopyTo(entry);
        }
    }
}
