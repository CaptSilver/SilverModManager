using System.Text;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using SteamKit2;

namespace NexusMods.Platform.Proton.Steam;

/// <summary>
/// Puts the launcher span into an app's launch options in <c>localconfig.vdf</c>.
/// </summary>
/// <remarks>
/// The file is parsed and written back by SteamKit2, so whitespace and escaping are normalised to what its
/// writer produces; keys and values outside the launch options are unchanged. The previous file is kept as
/// <c>localconfig.vdf.silvermod-bak</c>, replaced on each edit; a line given up by
/// <see cref="ReplaceLaunchOptions"/> is also appended to a record that is never overwritten.
/// </remarks>
public sealed class LocalConfigVdfWriter(IProcessTracker processTracker)
{
    private const string BackupSuffix = ".silvermod-bak";

    /// <summary>
    /// The outcome of an edit.
    /// </summary>
    /// <param name="Line">The launch options now stored, or null when refused.</param>
    /// <param name="Refusal">Why nothing was changed, or null on success.</param>
    /// <param name="Backup">The copy of the file as it was before the edit, or null when refused.</param>
    /// <param name="Replacement">
    /// On a refusal because another manager's line is stored: what <see cref="ReplaceLaunchOptions"/> would swap,
    /// so a caller can show both lines without reading the file again. On a successful replace: what was swapped.
    /// </param>
    /// <param name="ReplacedLineRecord">The file the replaced line was appended to, or null when nothing was replaced.</param>
    public sealed record Result(string? Line, string? Refusal, AbsolutePath? Backup, Replacement? Replacement = null, AbsolutePath? ReplacedLineRecord = null)
    {
        /// <summary>
        /// Whether the edit was refused.
        /// </summary>
        public bool IsRefused => Refusal is not null;
    }

    /// <summary>
    /// Another manager's launch line and the line that would take its place.
    /// </summary>
    /// <param name="ManagerName">The manager's name when it is a known one, else null.</param>
    /// <param name="OldLine">The line as stored.</param>
    /// <param name="NewLine">The line <see cref="ReplaceLaunchOptions"/> would store.</param>
    public sealed record Replacement(string? ManagerName, string OldLine, string NewLine);

    /// <summary>
    /// Sets the launch options of <paramref name="appId"/> so the game starts through the launcher for the
    /// loadout, keeping the user's own prefix and tail and carrying the DLL overrides the game needs. Refuses
    /// while Steam runs (it rewrites the file when it exits, discarding the edit), when the file is missing or
    /// unreadable, and when the existing line cannot be edited safely.
    /// </summary>
    public Result SetLauncherSpan(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides)
    {
        var (refusal, root, app, options) = OpenLaunchOptions(localConfig, appId);
        if (refusal is not null) return Refuse(refusal);
        var path = localConfig.ToString();

        var edit = SteamLaunchOptionLine.Rewrite(options?.Value ?? "", shimPath, loadoutName, overrides);
        if (edit.IsRefused) return RefuseWithReplacement(edit.Refusal!, options?.Value ?? "", shimPath, loadoutName, overrides);

        if (options is null) app!.Children.Add(options = new KeyValue("LaunchOptions"));
        options.Value = edit.Line;

        var backup = localConfig.Parent.Combine(localConfig.FileName + BackupSuffix);
        File.Copy(path, backup.ToString(), overwrite: true);

        WriteAtomically(path, root!);

        return VerifyWritten(localConfig, appId, shimPath, loadoutName, overrides, edit.Line!, backup) ?? new Result(edit.Line, null, backup);
    }

    /// <summary>
    /// Swaps another manager's launch line for the launcher span, but only if the stored line is still
    /// <paramref name="expectedOldLine"/>, the one the user was shown. The old line is appended to
    /// <paramref name="record"/> first and the <c>.silvermod-bak</c> copy is made second, so the line is safe in
    /// our own data before Steam's folder changes at all; a failure at either step is a refusal and leaves the
    /// config as it was.
    /// </summary>
    public Result ReplaceLaunchOptions(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, string expectedOldLine, AbsolutePath record)
    {
        var (refusal, root, app, options) = OpenLaunchOptions(localConfig, appId);
        if (refusal is not null) return Refuse(refusal);
        var path = localConfig.ToString();

        var current = options?.Value ?? "";
        if (!string.Equals(current, expectedOldLine, StringComparison.Ordinal))
        {
            return Refuse($"The launch options are no longer the line that was shown. They are now: {current}. Look at them again before replacing.");
        }

        if (SteamLaunchOptionLine.FindOtherManager(current) is not { } manager)
        {
            return Refuse($"Steam's launch options for {appId} are not another manager's line; use the ordinary write.");
        }

        var edit = SteamLaunchOptionLine.Replace(current, shimPath, loadoutName, overrides);
        if (edit.IsRefused) return Refuse(edit.Refusal!);

        try
        {
            record.Parent.CreateDirectory();
            File.AppendAllText(record.ToString(), RecordBlock(path, appId, current, edit.Line!), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse($"The replaced-line record {record} could not be written ({ex.Message}), so the launch options were left alone.");
        }

        var backup = localConfig.Parent.Combine(localConfig.FileName + BackupSuffix);
        try { File.Copy(path, backup.ToString(), overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse($"The backup copy {backup} could not be written ({ex.Message}), so the launch options were left alone. The old line is in {record}.");
        }

        options!.Value = edit.Line;
        try { WriteAtomically(path, root!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse($"Steam's {path} could not be written ({ex.Message}), so the launch options were left alone. The old line is in {record}.");
        }

        var replacement = new Replacement(manager.Name, current, edit.Line!);
        return VerifyWritten(localConfig, appId, shimPath, loadoutName, overrides, edit.Line!, backup)
               ?? new Result(edit.Line, null, backup, replacement, record);
    }

    private static string RecordBlock(string configPath, uint appId, string oldLine, string newLine)
        => $"replaced {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\nconfig {configPath}\napp {appId}\nold {oldLine}\nnew {newLine}\n\n";

    /// <summary>
    /// The checks both edits share before touching the file, and the nodes that hold the app's launch options
    /// (<c>Options</c> is null when the app has none yet).
    /// </summary>
    private (string? Refusal, KeyValue? Root, KeyValue? App, KeyValue? Options) OpenLaunchOptions(AbsolutePath localConfig, uint appId)
    {
        if (processTracker.IsSteamRunning())
        {
            return ("Steam rewrites its config on exit, so a launch option written while Steam is running " +
                    "is lost. Close Steam completely (including the tray icon) and try again.", null, null, null);
        }

        var path = localConfig.ToString();
        if (!localConfig.FileExists) return ($"Steam's {path} does not exist, so there is nowhere to store the launch options.", null, null, null);

        KeyValue? root;
        try { root = VdfFile.Read(localConfig); }
        catch (Exception ex) when (ex is not OperationCanceledException) { root = null; }
        if (root is null) return ($"Steam's {path} could not be parsed, so it was left alone.", null, null, null);

        var app = GetOrAddSection(GetOrAddSection(GetOrAddSection(GetOrAddSection(root, "Software"), "Valve"), "Steam"), "apps");
        app = GetOrAddSection(app, appId.ToString());
        return (null, root, app, app.Children.FirstOrDefault(c => IsNamed(c, "LaunchOptions")));
    }

    /// <summary>
    /// Reads the file back the way the Health Check will, and puts the backup in place of the config when the
    /// stored line is not the one written. Null when it reads back.
    /// </summary>
    private static Result? VerifyWritten(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, string expectedLine, AbsolutePath backup)
    {
        var read = LocalConfigLaunchOptions.Read(localConfig, appId);
        var parsed = read.Line is null ? null : SteamLaunchOptionLine.Parse(read.Line);
        if (read.Line == expectedLine && parsed is { HasSpan: true } && parsed.ShimPath == shimPath && parsed.LoadoutName == loadoutName && ReadsBack(read.Line!, overrides)) return null;

        File.Copy(backup.ToString(), localConfig.ToString(), overwrite: true);
        return Refuse($"Steam's {localConfig} did not read back the launch options that were written, so the original was restored.");
    }

    private static Result RefuseWithReplacement(string refusal, string current, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides)
    {
        if (SteamLaunchOptionLine.FindOtherManager(current) is not { } manager) return Refuse(refusal);

        var replace = SteamLaunchOptionLine.Replace(current, shimPath, loadoutName, overrides);
        return replace.IsRefused ? Refuse(refusal) : new Result(null, refusal, null, new Replacement(manager.Name, current, replace.Line!));
    }

    /// <summary>
    /// Whether the Health Check's own reader finds every required entry in the stored line, so a line that
    /// passes is one the Health Check accepts.
    /// </summary>
    private static bool ReadsBack(string line, IReadOnlyList<WineDllOverride> required)
    {
        if (required.Count == 0) return true;

        var stored = WineParser.ParseEnvironmentVariable(WineParser.GetWineDllOverridesSection(line));
        return required.All(entry => stored.Any(s =>
            string.Equals(s.DllName, entry.DllName, StringComparison.OrdinalIgnoreCase) && s.OverrideTypes.SequenceEqual(entry.OverrideTypes)));
    }

    private static Result Refuse(string refusal) => new(null, refusal, null);

    private static bool IsNamed(KeyValue node, string name) => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase);

    private static KeyValue GetOrAddSection(KeyValue parent, string name)
    {
        var existing = parent.Children.FirstOrDefault(c => IsNamed(c, name));
        if (existing is not null) return existing;

        var created = new KeyValue(name);
        parent.Children.Add(created);
        return created;
    }

    /// <summary>
    /// Writes beside the file and renames over it, so a crash never leaves half a config. The original's
    /// permissions carry over.
    /// </summary>
    private static void WriteAtomically(string path, KeyValue root)
    {
        var temp = path + ".silvermod-tmp";
        using (var stream = File.Create(temp)) root.SaveToStream(stream, asBinary: false);
        File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        File.Move(temp, path, overwrite: true);
    }
}
