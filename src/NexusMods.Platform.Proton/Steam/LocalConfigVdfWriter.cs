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
/// <c>localconfig.vdf.silvermod-bak</c>, replaced on each edit.
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
    public sealed record Result(string? Line, string? Refusal, AbsolutePath? Backup)
    {
        /// <summary>
        /// Whether the edit was refused.
        /// </summary>
        public bool IsRefused => Refusal is not null;
    }

    /// <summary>
    /// Sets the launch options of <paramref name="appId"/> so the game starts through the launcher for the
    /// loadout, keeping the user's own prefix and tail and carrying the DLL overrides the game needs. Refuses
    /// while Steam runs (it rewrites the file when it exits, discarding the edit), when the file is missing or
    /// unreadable, and when the existing line cannot be edited safely.
    /// </summary>
    public Result SetLauncherSpan(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides)
    {
        if (processTracker.IsSteamRunning())
        {
            return Refuse("Steam rewrites its config on exit, so a launch option written while Steam is running " +
                          "is lost. Close Steam completely (including the tray icon) and try again.");
        }

        var path = localConfig.ToString();
        if (!localConfig.FileExists) return Refuse($"Steam's {path} does not exist, so there is nowhere to store the launch options.");

        KeyValue? root;
        try { root = VdfFile.Read(localConfig); }
        catch (Exception ex) when (ex is not OperationCanceledException) { root = null; }
        if (root is null) return Refuse($"Steam's {path} could not be parsed, so it was left alone.");

        var app = GetOrAddSection(GetOrAddSection(GetOrAddSection(GetOrAddSection(root, "Software"), "Valve"), "Steam"), "apps");
        app = GetOrAddSection(app, appId.ToString());
        var options = app.Children.FirstOrDefault(c => IsNamed(c, "LaunchOptions"));

        var edit = SteamLaunchOptionLine.Rewrite(options?.Value ?? "", shimPath, loadoutName, overrides);
        if (edit.IsRefused) return Refuse(edit.Refusal!);

        if (options is null) app.Children.Add(options = new KeyValue("LaunchOptions"));
        options.Value = edit.Line;

        var backup = localConfig.Parent.Combine(localConfig.FileName + BackupSuffix);
        File.Copy(path, backup.ToString(), overwrite: true);

        WriteAtomically(path, root);

        var read = LocalConfigLaunchOptions.Read(localConfig, appId);
        var parsed = read.Line is null ? null : SteamLaunchOptionLine.Parse(read.Line);
        if (read.Line != edit.Line || parsed is not { HasSpan: true } || parsed.ShimPath != shimPath || parsed.LoadoutName != loadoutName || !ReadsBack(read.Line!, overrides))
        {
            File.Copy(backup.ToString(), path, overwrite: true);
            return Refuse($"Steam's {path} did not read back the launch options that were written, so the original was restored.");
        }

        return new Result(edit.Line, null, backup);
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
