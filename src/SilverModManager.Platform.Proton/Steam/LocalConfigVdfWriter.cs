using System.Diagnostics.CodeAnalysis;
using System.Text;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Steam;
using SteamKit2;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// Puts the launcher span into an app's launch options in <c>localconfig.vdf</c>.
/// </summary>
/// <remarks>
/// The file is parsed and written back by SteamKit2, so whitespace and escaping are normalised to what its
/// writer produces; keys and values outside the launch options are unchanged. The file is written through
/// <see cref="SteamConfigFileEditor"/>: the first version this app saw is kept as <c>localconfig.vdf.silvermod-orig</c>,
/// the previous one as <c>localconfig.vdf.silvermod-bak</c>, replaced on each edit; a line given up by
/// <see cref="ReplaceLaunchOptions"/> is also appended to a record that is never overwritten.
/// </remarks>
public sealed class LocalConfigVdfWriter(SteamConfigFileEditor editor)
{
    private const string MissingFileRefusalFormat = "Steam's {0} does not exist, so there is nowhere to store the launch options.";

    /// <summary>
    /// For callers that have only a process tracker. Dependency injection is given the primary constructor through a factory.
    /// </summary>
    public LocalConfigVdfWriter(IProcessTracker processTracker) : this(new SteamConfigFileEditor(processTracker)) { }

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
    /// <param name="SteamStartedDuringWrite">Steam started while the file was being written, so it may rewrite the file and lose the edit.</param>
    public sealed record Result(string? Line, string? Refusal, AbsolutePath? Backup, Replacement? Replacement = null, AbsolutePath? ReplacedLineRecord = null, bool SteamStartedDuringWrite = false)
    {
        /// <summary>
        /// Whether the edit was refused.
        /// </summary>
        [MemberNotNullWhen(true, nameof(Refusal))]
        [MemberNotNullWhen(false, nameof(Line))]
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
    /// loadout, keeping the user's own prefix and tail and carrying the DLL overrides the game needs and the
    /// arguments that make it skip its own launcher. Refuses while Steam runs (it rewrites the file when it
    /// exits, discarding the edit), when the file is missing or unreadable, and when the existing line cannot be
    /// edited safely.
    /// </summary>
    public Result SetLauncherSpan(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        string? line = null;
        Result? withReplacement = null;
        var edited = editor.Edit(localConfig,
            bytes =>
            {
                var location = OpenLaunchOptions(localConfig, bytes, appId);
                if (location.IsRefused) return (null, location.Refusal);
                var (root, app, options) = (location.Root, location.App, location.Options);

                var edit = SteamLaunchOptionLine.Rewrite(options?.Value ?? "", shimPath, loadoutName, overrides, launcherSkipArguments, environment);
                if (edit.IsRefused)
                {
                    withReplacement = RefuseWithReplacement(edit.Refusal, options?.Value ?? "", shimPath, loadoutName, overrides, launcherSkipArguments, environment);
                    return (null, edit.Refusal);
                }

                if (options is null) app.Children.Add(options = new KeyValue("LaunchOptions"));
                options.Value = line = edit.Line;
                return (Serialize(root), null);
            },
            _ => line is not null && ReadsBackAs(localConfig, appId, shimPath, loadoutName, overrides, line),
            missingFileRefusal: string.Format(MissingFileRefusalFormat, localConfig),
            mismatchRefusal: $"Steam's {localConfig} did not read back the launch options that were written, so the original was restored.");

        return withReplacement ?? ToResult(edited, line);
    }

    /// <summary>
    /// Swaps another manager's launch line for the launcher span, but only if the stored line is still
    /// <paramref name="expectedOldLine"/>, the one the user was shown. The old line is appended to
    /// <paramref name="record"/> first and the <c>.silvermod-bak</c> copy is made second, so the line is safe in
    /// our own data before Steam's folder changes at all; a failure at either step is a refusal and leaves the
    /// config as it was.
    /// </summary>
    public Result ReplaceLaunchOptions(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, string expectedOldLine, AbsolutePath record, IReadOnlyList<KeyValuePair<string, string>>? environment = null)
    {
        string? line = null;
        string? managerName = null;
        var recordWritten = false;
        var edited = editor.Edit(localConfig,
            bytes =>
            {
                var location = OpenLaunchOptions(localConfig, bytes, appId);
                if (location.IsRefused) return (null, location.Refusal);
                var root = location.Root;

                var current = location.Options?.Value ?? "";
                if (!string.Equals(current, expectedOldLine, StringComparison.Ordinal))
                {
                    return (null, $"The launch options are no longer the line that was shown. They are now: {current}. Look at them again before replacing.");
                }

                // No LaunchOptions node reads as an empty line, which is never another manager's.
                if (location.Options is not { } options || SteamLaunchOptionLine.FindOtherManager(current) is not { } manager)
                {
                    return (null, $"Steam's launch options for {appId} are not another manager's line; use the ordinary write.");
                }

                var edit = SteamLaunchOptionLine.Replace(current, shimPath, loadoutName, overrides, launcherSkipArguments, environment);
                if (edit.IsRefused) return (null, edit.Refusal);

                try
                {
                    record.Parent.CreateDirectory();
                    File.AppendAllText(record.ToString(), RecordBlock(localConfig.ToString(), appId, current, edit.Line), new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (null, $"The replaced-line record {record} could not be written ({ex.Message}), so the launch options were left alone.");
                }
                recordWritten = true;

                options.Value = line = edit.Line;
                managerName = manager.Name;
                return (Serialize(root), null);
            },
            _ => line is not null && ReadsBackAs(localConfig, appId, shimPath, loadoutName, overrides, line),
            missingFileRefusal: string.Format(MissingFileRefusalFormat, localConfig),
            mismatchRefusal: $"Steam's {localConfig} did not read back the launch options that were written, so the original was restored.");

        if (edited.IsRefused && recordWritten) return Refuse($"{edited.Refusal} The old line is in {record}.");

        var result = ToResult(edited, line);
        return result.IsRefused ? result : result with { Replacement = new Replacement(managerName, expectedOldLine, line ?? throw new InvalidOperationException("A written replacement has a line.")), ReplacedLineRecord = record };
    }

    private static Result ToResult(SteamConfigEdit edited, string? line)
        => edited.Refusal is { } refusal ? Refuse(refusal) : new Result(line, null, edited.Backup, SteamStartedDuringWrite: edited.SteamStartedDuringWrite);

    private static string RecordBlock(string configPath, uint appId, string oldLine, string newLine)
        => $"replaced {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\nconfig {configPath}\napp {appId}\nold {oldLine}\nnew {newLine}\n\n";

    /// <summary>
    /// Where the app's launch options live, or why they cannot be edited. <c>Options</c> is null when the app has
    /// none yet.
    /// </summary>
    private sealed record LaunchOptionsLocation(string? Refusal, KeyValue? Root, KeyValue? App, KeyValue? Options)
    {
        [MemberNotNullWhen(true, nameof(Refusal))]
        [MemberNotNullWhen(false, nameof(Root), nameof(App))]
        public bool IsRefused => Refusal is not null;

        public static LaunchOptionsLocation Refused(string refusal) => new(refusal, null, null, null);
    }

    /// <summary>
    /// Parses the file and finds the nodes that hold the app's launch options (<c>Options</c> is null when the app
    /// has none yet).
    /// </summary>
    private static LaunchOptionsLocation OpenLaunchOptions(AbsolutePath localConfig, byte[] bytes, uint appId)
    {
        KeyValue? root;
        try { root = KeyValue.LoadFromString(Encoding.UTF8.GetString(bytes)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { root = null; }
        if (root is null) return LaunchOptionsLocation.Refused($"Steam's {localConfig} could not be parsed, so it was left alone.");

        var app = GetOrAddSection(GetOrAddSection(GetOrAddSection(GetOrAddSection(root, "Software"), "Valve"), "Steam"), "apps");
        app = GetOrAddSection(app, appId.ToString());
        return new LaunchOptionsLocation(null, root, app, app.Children.FirstOrDefault(c => IsNamed(c, "LaunchOptions")));
    }

    /// <summary>
    /// Whether the file reads back the way the Health Check will read it, with the line that was written.
    /// </summary>
    private static bool ReadsBackAs(AbsolutePath localConfig, uint appId, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, string expectedLine)
    {
        var read = LocalConfigLaunchOptions.Read(localConfig, appId);
        var parsed = read.Line is null ? null : SteamLaunchOptionLine.Parse(read.Line);
        return read.Line == expectedLine && parsed is { HasSpan: true } && parsed.ShimPath == shimPath && parsed.LoadoutName == loadoutName && ReadsBack(expectedLine, overrides);
    }

    private static Result RefuseWithReplacement(string refusal, string current, string shimPath, string loadoutName, IReadOnlyList<WineDllOverride> overrides, IReadOnlyList<string> launcherSkipArguments, IReadOnlyList<KeyValuePair<string, string>>? environment)
    {
        if (SteamLaunchOptionLine.FindOtherManager(current) is not { } manager) return Refuse(refusal);

        var replace = SteamLaunchOptionLine.Replace(current, shimPath, loadoutName, overrides, launcherSkipArguments, environment);
        return replace.IsRefused ? Refuse(refusal) : new Result(null, refusal, null, new Replacement(manager.Name, current, replace.Line));
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

    private static byte[] Serialize(KeyValue root)
    {
        using var stream = new MemoryStream();
        root.SaveToStream(stream, asBinary: false);
        return stream.ToArray();
    }
}
