using System.Text.RegularExpressions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Protontricks;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Reads what a tool's Wine output says about why it stopped. Two failures are recognised: a DLL the prefix
/// lacks (<c>err:module:import_dll</c>) and protontricks' Flatpak not seeing the tool's folder. A .NET Framework
/// failure (<c>err:seh:NtRaiseException … 80131506</c>) has no matcher; it reaches the player through
/// <see cref="Significant"/> only.
/// </summary>
public static partial class WineOutputHints
{
    private const string UnixPathUnreadable = "Could not translate the specified Unix filename to a DOS filename.";

    [GeneratedRegex("""^[0-9a-f]+:err:module:import_dll Library (?<dll>\S+) \(which is needed by L"(?<by>[^"]*)"\) not found""", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ImportDll();

    [GeneratedRegex(@"^[0-9a-f]+:err:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrLine();

    /// <summary>The sentence(s) that say why the tool stopped, or null when nothing in the output is recognised.</summary>
    public static string? Explain(ToolRunResult result, GameInstallation installation, AbsolutePath exe, ResolvedPrefix? prefix)
    {
        var sentences = new List<string>();
        var game = MarkdownText.InlineMidLine(installation.Game.DisplayName);

        var dlls = ImportDll().Matches(result.StderrTail)
            .Select(m => m.Groups["dll"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dlls)
            sentences.Add(MissingDll(dll, game, prefix));

        // The Flatpak build of protontricks only sees what it was granted, so a tool in an unshared folder has no DOS path.
        if (result.StdoutTail.Contains(UnixPathUnreadable, StringComparison.Ordinal) && result.Strategy == ProtontricksStrategy.StrategyName)
        {
            var folder = exe.Parent;
            var command = MarkdownText.Code($"flatpak override --user --filesystem={ShellText.Quote(folder.ToString())} {ProtontricksFlatpakDependency.FlatpakPackageId}");
            var sentence = $"protontricks runs in a Flatpak that cannot see {MarkdownText.Code(folder)}. Let it: {command}";
            if (result.FallbackReason is { } reason) sentence += $" Proton did not run it because {reason}.";
            sentences.Add(sentence);
        }

        return sentences.Count == 0 ? null : string.Join(' ', sentences);
    }

    private static string MissingDll(string dll, string game, ResolvedPrefix? prefix)
    {
        var needs = $"It needs {MarkdownText.Code(dll)}";
        var missing = $"which is not in {game}'s Wine prefix.";

        // ForNativeFile compares ignoring case, so MSVBVM60.DLL in the log finds the lower-case catalogue entry.
        if (PrefixComponents.ForNativeFile(dll) is not { } component) return $"{needs}, {missing}";

        if (component.Installable)
            return $"{needs} (the {component.DisplayName}), {missing} Install the {component.DisplayName} from the Tools page's prerequisite prompt or the Health Check.";

        return prefix is null
            ? $"{needs} (the {component.DisplayName}), {missing}"
            : $"{needs} (the {component.DisplayName}), {missing} Install it with {MarkdownText.Code(PrefixInstallCommand.For(prefix, component.Id.Value))}.";
    }

    /// <summary>err: lines minus err:hid, newest last, at most max; the last max raw lines when there are none.</summary>
    public static IReadOnlyList<string> Significant(string stderrTail, string stdoutTail, int max = 8)
    {
        var stderr = Lines(stderrTail);
        var stdout = Lines(stdoutTail);

        var errors = stderr.Concat(stdout)
            .Where(line => ErrLine().IsMatch(line) && !line.Contains(":err:hid:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (errors.Count > 0) return errors.TakeLast(max).ToList();

        return stderr.Concat(stdout).TakeLast(max).ToList();
    }

    private static List<string> Lines(string text)
        => text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
}
