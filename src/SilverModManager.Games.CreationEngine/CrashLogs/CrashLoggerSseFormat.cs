using System.Globalization;
using System.Text.RegularExpressions;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// Reads CrashLoggerSSE logs. The files mix CRLF and LF (the STACK and REGISTERS expansion is LF, the rest CRLF), so
/// the text is split on LF and every line loses a trailing CR. The scan is one pass over the lines with no look-back,
/// which is also why a log cut off mid-section still gives a report.
/// </summary>
public sealed partial class CrashLoggerSseFormat : ICrashLogFormat
{
    private const string LoggerPrefix = "CrashLoggerSSE v";

    /// <summary>A pointer-typed stack slot whose type is not a game object.</summary>
    private static readonly HashSet<string> NonObjectTypes = new(StringComparer.Ordinal) { "void", "char", "wchar_t" };

    public bool CanParse(ReadOnlySpan<char> firstLines)
    {
        var line = 0;
        while (line < 2)
        {
            var newline = firstLines.IndexOf('\n');
            if (newline < 0) return false;
            firstLines = firstLines[(newline + 1)..];
            line++;
        }
        return firstLines.StartsWith(LoggerPrefix, StringComparison.Ordinal);
    }

    public CrashReport Parse(string text)
    {
        var state = new ParseState();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (i < 3 && state.ReadPreamble(i, line)) continue;
            state.Read(line);
        }
        return state.ToReport();
    }

    private enum Section
    {
        Preamble,
        Skipped,
        AccessViolation,
        RelevantObjects,
        RecentModelRequests,
        ModelPaths,
        ProcessInfo,
        ThreadContext,
        CallStack,
        Registers,
        Stack,
        SksePlugins,
        GameState,
        Plugins,
        MissingMasters,
    }

    /// <summary>Section keys, compared by prefix against the upper-cased text before the first parenthesis.</summary>
    private static readonly (string Key, Section Section)[] SectionKeys =
    [
        ("ACCESS VIOLATION ANALYSIS", Section.AccessViolation),
        ("POSSIBLE RELEVANT OBJECTS", Section.RelevantObjects),
        ("RECENT MODEL REQUESTS", Section.RecentModelRequests),
        ("MODEL PATHS", Section.ModelPaths),
        ("PROCESS INFO", Section.ProcessInfo),
        ("THREAD CONTEXT", Section.ThreadContext),
        ("CALL STACK", Section.CallStack),
        ("REGISTERS", Section.Registers),
        ("STACK", Section.Stack),
        ("SKSE PLUGINS", Section.SksePlugins),
        ("GAME STATE", Section.GameState),
        ("PLUGINS", Section.Plugins),
        ("MISSING MASTERS", Section.MissingMasters),
    ];

    private sealed class ParseState
    {
        private Section _section = Section.Preamble;

        private string? _crashTime, _gameVersion, _loggerVersion, _exceptionCode, _faultModule, _faultOffset, _faultInstruction, _threadRole;
        private TimeSpan? _uptime;
        private int? _pluginCount;

        private readonly List<CrashFrame> _frames = [];
        private readonly List<RelevantObject> _relevantObjects = [];
        private readonly List<ObjectDetails> _objectDetails = [];
        private readonly List<string> _recentModelRequests = [];
        private readonly List<string> _resourcePaths = [];
        private readonly List<SksePlugin> _sksePlugins = [];
        private readonly List<string> _plugins = [];
        private readonly List<string> _missingMasters = [];
        private readonly List<string> _gameState = [];

        private OpenObject? _open;

        /// <summary>An object of STACK: or REGISTERS: whose nested lines are still arriving.</summary>
        private sealed class OpenObject(string location, string type, string? name, string? parenthetical)
        {
            public readonly List<DetailLine> Own = [];
            public readonly List<DetailLine> Parents = [];
            public bool InParents;

            public ObjectDetails ToDetails() => new(location, type, name, parenthetical, Own, Parents);
        }

        public bool ReadPreamble(int index, string line)
        {
            switch (index)
            {
                case 0 when line.StartsWith("CRASH TIME:", StringComparison.Ordinal):
                    _crashTime = line["CRASH TIME:".Length..].Trim();
                    return true;
                case 1:
                    _gameVersion = line.Trim();
                    return true;
                case 2 when line.StartsWith(LoggerPrefix, StringComparison.Ordinal):
                    _loggerVersion = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
                    return true;
                default:
                    return false;
            }
        }

        public void Read(string line)
        {
            if (IsSectionHeader(line))
            {
                CloseObject();
                _section = SectionOf(line);
                return;
            }

            switch (_section)
            {
                case Section.Preamble: ReadException(line); break;
                case Section.AccessViolation: ReadAccessViolation(line); break;
                case Section.RelevantObjects: ReadRelevantObject(line); break;
                case Section.RecentModelRequests: ReadRecentModelRequest(line); break;
                case Section.ModelPaths: ReadModelPath(line); break;
                case Section.ProcessInfo: ReadProcessInfo(line); break;
                case Section.ThreadContext: ReadThreadContext(line); break;
                case Section.CallStack: ReadFrame(line); break;
                case Section.Registers:
                case Section.Stack: ReadExpansion(line); break;
                case Section.SksePlugins: ReadSksePlugin(line); break;
                case Section.GameState: ReadGameState(line); break;
                case Section.Plugins: ReadPlugin(line); break;
                case Section.MissingMasters: ReadMissingMaster(line); break;
            }
        }

        public CrashReport ToReport()
        {
            CloseObject();
            return new CrashReport
            {
                CrashTime = _crashTime,
                GameVersion = _gameVersion,
                LoggerVersion = _loggerVersion,
                ExceptionCode = _exceptionCode,
                FaultModule = _faultModule,
                FaultOffset = _faultOffset,
                FaultInstruction = _faultInstruction,
                Uptime = _uptime,
                ThreadRole = _threadRole,
                Frames = _frames,
                RelevantObjects = _relevantObjects,
                ObjectDetails = _objectDetails,
                RecentModelRequests = _recentModelRequests,
                ResourcePaths = _resourcePaths,
                SksePlugins = _sksePlugins,
                Plugins = _plugins,
                PluginCount = _pluginCount,
                MissingMasters = _missingMasters,
                GameState = _gameState,
            };
        }

        private static bool IsSectionHeader(string line) =>
            line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.TrimEnd().EndsWith(':');

        private static Section SectionOf(string header)
        {
            var paren = header.IndexOf('(');
            var key = (paren < 0 ? header : header[..paren]).Trim().TrimEnd(':').ToUpperInvariant();
            foreach (var (prefix, section) in SectionKeys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return section;
            }
            return Section.Skipped;
        }

        private void ReadException(string line)
        {
            var match = ExceptionHeader().Match(line);
            if (!match.Success) return;
            _exceptionCode = match.Groups["code"].Value;
            _faultModule = match.Groups["module"].Value;
            _faultOffset = match.Groups["offset"].Value;
            _faultInstruction = NullIfEmpty(match.Groups["instr"].Value);
        }

        private void ReadAccessViolation(string line)
        {
            if (_faultInstruction is not null) return;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Instruction:", StringComparison.Ordinal))
                _faultInstruction = NullIfEmpty(trimmed["Instruction:".Length..].Trim());
        }

        private void ReadRelevantObject(string line)
        {
            var match = RelevantObjectLine().Match(line);
            if (!match.Success) return;
            _relevantObjects.Add(new RelevantObject(
                match.Groups["loc"].Value,
                match.Groups["type"].Value,
                OptionalGroup(match, "name"),
                OptionalGroup(match, "form"),
                OptionalGroup(match, "paren")));
        }

        private void ReadRecentModelRequest(string line)
        {
            const string marker = " ago  ";
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return;
            var path = line[(at + marker.Length)..].Trim();
            if (path.Length > 0) _recentModelRequests.Add(path);
        }

        private void ReadModelPath(string line)
        {
            var at = line.IndexOf("] ", StringComparison.Ordinal);
            if (at < 0) return;
            var path = line[(at + 2)..].Trim();
            if (path.Length > 0) _resourcePaths.Add(path);
        }

        private void ReadProcessInfo(string line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Process Uptime:", StringComparison.Ordinal)) return;
            var match = UptimeMilliseconds().Match(trimmed);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
                _uptime = TimeSpan.FromMilliseconds(ms);
        }

        private void ReadThreadContext(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Likely Role:", StringComparison.Ordinal))
                _threadRole = NullIfEmpty(trimmed["Likely Role:".Length..].Trim());
        }

        private void ReadFrame(string line)
        {
            // Two steps: the pipe separates the address part from the symbol, and the instruction can be empty.
            const string pipe = " |  ";
            var at = line.IndexOf(pipe, StringComparison.Ordinal);
            var left = at < 0 ? line : line[..at];
            var right = at < 0 ? null : line[(at + pipe.Length)..];

            var match = FrameAddress().Match(left);
            if (!match.Success) return;

            var module = match.Groups["module"].Value;
            var id = OptionalGroup(match, "id");
            string? symbol = null;
            if (right is not null)
            {
                if (module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var exe = ExecutableSymbol().Match(right);
                    symbol = NullIfEmpty(AddressSuffix().Replace(exe.Groups["sym"].Value, ""));
                    id ??= OptionalGroup(exe, "id");
                }
                else
                {
                    symbol = NullIfEmpty(right);
                }
            }

            _frames.Add(new CrashFrame(
                int.Parse(match.Groups["i"].Value, CultureInfo.InvariantCulture),
                match.Groups["k"].Value[0],
                module,
                match.Groups["offset"].Value,
                NullIfEmpty(match.Groups["instr"].Value.Trim()),
                symbol,
                id));
        }

        private void ReadExpansion(string line)
        {
            var depth = 0;
            while (depth < line.Length && line[depth] == '\t') depth++;
            if (depth == 0 || depth >= line.Length) return;

            if (depth == 1)
            {
                CloseObject();
                var match = ExpansionObject().Match(line);
                if (!match.Success) return;
                var type = match.Groups["type"].Value;
                if (NonObjectTypes.Contains(type)) return;
                var location = match.Groups["loc"].Success ? match.Groups["loc"].Value : match.Groups["reg"].Value;
                _open = new OpenObject(location, type, OptionalGroup(match, "name"), OptionalGroup(match, "paren"));
                return;
            }

            if (_open is null) return;
            var detail = line[depth..];
            var colon = detail.IndexOf(':');
            if (colon <= 0) return;

            var key = detail[..colon].Trim();
            // The first Checking Parent at depth two is where the object's own block ends; the parent blocks nest
            // deeper from there, so everything after it belongs to the parents.
            if (depth == 2 && key == "Checking Parent") _open.InParents = true;
            (_open.InParents ? _open.Parents : _open.Own).Add(new DetailLine(key, Unquote(detail[(colon + 1)..].Trim())));
        }

        private void ReadSksePlugin(string line)
        {
            var match = SksePluginLine().Match(line);
            if (match.Success) _sksePlugins.Add(new SksePlugin(match.Groups["name"].Value, OptionalGroup(match, "ver")));
        }

        private void ReadGameState(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) _gameState.Add(trimmed);
        }

        private void ReadPlugin(string line)
        {
            var total = PluginTotal().Match(line);
            if (total.Success)
            {
                _pluginCount = int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture);
                return;
            }
            var match = PluginLine().Match(line);
            if (match.Success) _plugins.Add(match.Groups["name"].Value.Trim());
        }

        private void ReadMissingMaster(string line)
        {
            const string suffix = ": NOT FOUND";
            var trimmed = line.Trim();
            if (!trimmed.EndsWith(suffix, StringComparison.Ordinal)) return;
            var chain = trimmed[..^suffix.Length];
            var arrow = chain.LastIndexOf(" -> ", StringComparison.Ordinal);
            var name = (arrow < 0 ? chain : chain[(arrow + 4)..]).Trim();
            if (name.Length > 0 && !_missingMasters.Contains(name)) _missingMasters.Add(name);
        }

        private void CloseObject()
        {
            if (_open is null) return;
            _objectDetails.Add(_open.ToDetails());
            _open = null;
        }

        private static string? OptionalGroup(Match match, string name) =>
            match.Groups[name].Success ? match.Groups[name].Value : null;

        private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

        private static string Unquote(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    // The header's symbol is not kept: it ends in a stray ')' and the call stack's first frame says the same thing.
    [GeneratedRegex(@"^Unhandled exception ""(?<code>[^""]+)"" at 0x[0-9A-Fa-f]+ (?<module>\S+)\+(?<offset>[0-9A-Fa-f]+)(?:\t(?<instr>[^|]*?))?(?:\s*\|.*)?\s*$")]
    private static partial Regex ExceptionHeader();

    [GeneratedRegex(@"^\t(?<loc>[^:]+): \((?<type>[^*)]+)\*\)(?: ""(?<name>[^""]*)"")?(?: \[(?<form>0x[0-9A-Fa-f]+)\])?(?: \(""(?<paren>[^""]*)""\))?")]
    private static partial Regex RelevantObjectLine();

    [GeneratedRegex(@"^\t\[\s*(?<i>\d+)\]\[(?<k>[PS])\] 0x[0-9A-Fa-f]+\s+(?<module>\S+)\+(?<offset>[0-9A-Fa-f]+)(?: -> (?<id>\d+)\+0x[0-9A-Fa-f]+)?(?:\t(?<instr>.*))?$")]
    private static partial Regex FrameAddress();

    [GeneratedRegex(@"^(?<sym>.*?)(?: -> (?<id>\d+)\+0x[0-9A-Fa-f]+)?(?:\t.*)?$")]
    private static partial Regex ExecutableSymbol();

    /// <summary>Ghidra's <c>_14xxxxxxx</c> function address and <c>_xxxxxx</c> offset, which end every auto-generated name.</summary>
    [GeneratedRegex(@"(?:_14[0-9A-Fa-f]{7})?(?:_[0-9A-Fa-f]{3,8})?$")]
    private static partial Regex AddressSuffix();

    [GeneratedRegex(@"^\t(?:\[(?<loc>RSP\+[0-9A-Fa-f]+)\s*\]|(?<reg>[A-Z0-9]{2,3})) 0x[0-9A-Fa-f]+\s+\((?<type>[^*)]+)\*\)(?: ""(?<name>[^""]*)"")?(?: \[0x[0-9A-Fa-f]+\])?(?: \(""(?<paren>[^""]*)""\))?")]
    private static partial Regex ExpansionObject();

    [GeneratedRegex(@"\((\d+)ms\)")]
    private static partial Regex UptimeMilliseconds();

    [GeneratedRegex(@"^\t(?<name>.+?\.dll)(?: v(?<ver>\S+))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SksePluginLine();

    [GeneratedRegex(@"Total:\s*(\d+)")]
    private static partial Regex PluginTotal();

    [GeneratedRegex(@"^\t\[[^\]]+\]\s+(?<name>.+)$")]
    private static partial Regex PluginLine();
}
