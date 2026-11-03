using System.IO.Hashing;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SilverModManager.Games.Bethesda.Loot.Database;
using NexusMods.Paths;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Games.Bethesda.Loot.Conditions;

// Follows loot-condition-interpreter's lib.rs (Expression::eval) and function/eval.rs.

/// <summary>
/// Evaluates conditions against an <see cref="IConditionContext"/>. Keeps what it has worked out: parsed
/// conditions, compiled patterns, checksums, and the result of every function but the cheap ones, so a masterlist
/// that repeats the same checks thousands of times reads each file once. Use one per sort; a file that changes
/// afterwards is not seen.
/// </summary>
public sealed class ConditionEvaluator : IConditionEvaluator
{
    private readonly IConditionContext _context;
    private readonly HashSet<string> _active;
    private readonly Dictionary<string, ConditionExpression> _parsed = [];
    private readonly Dictionary<string, Regex> _patterns = [];
    private readonly Dictionary<string, uint> _checksums = [];
    private readonly Dictionary<ConditionFunction, bool> _results = [];

    private readonly ILogger? _logger;
    private readonly HashSet<string> _refused = [];

    public ConditionEvaluator(IConditionContext context, ILogger? logger = null)
    {
        _context = context;
        _logger = logger;
        _active = new HashSet<string>(context.ActivePlugins.Select(name => name.ToLowerInvariant()));
    }

    public bool Evaluate(string condition)
    {
        if (!_parsed.TryGetValue(condition, out var expression))
            _parsed[condition] = expression = ConditionExpression.Parse(condition);

        return Evaluate(expression);
    }

    public bool Evaluate(ConditionExpression expression)
    {
        foreach (var compound in expression.Alternatives)
        {
            if (compound.Terms.All(Evaluate)) return true;
        }

        return false;
    }

    private bool Evaluate(ConditionTerm term) => term switch
    {
        ConditionTerm.OfFunction f => Evaluate(f.Function) != f.Negated,
        ConditionTerm.OfGroup g => Evaluate(g.Expression) != g.Negated,
        _ => throw new ArgumentOutOfRangeException(nameof(term), term, null),
    };

    private bool Evaluate(ConditionFunction function)
    {
        // The active-plugin functions and checksums have their own lookups, which are as cheap as the result cache.
        var cacheable = function is not (ConditionFunction.ActivePath or ConditionFunction.ActiveRegex
            or ConditionFunction.ManyActive or ConditionFunction.Checksum);
        if (cacheable && _results.TryGetValue(function, out var cached)) return cached;

        var result = Compute(function);
        if (cacheable) _results[function] = result;
        return result;
    }

    private bool Compute(ConditionFunction function) => function switch
    {
        ConditionFunction.FilePath f => Resolve(f.Path) is { } path && Exists(path),
        ConditionFunction.FileRegex f => Entries(f.ParentPath).Any(IsMatch(f.Pattern)),
        ConditionFunction.Many f => Entries(f.ParentPath).Where(IsMatch(f.Pattern)).Take(2).Count() == 2,
        ConditionFunction.FileSize f => FileSize(f),
        ConditionFunction.Readable f => Resolve(f.Path) is { } path && IsReadable(path),
        ConditionFunction.IsExecutable f => IsExecutable(f),
        ConditionFunction.ActivePath f => IsPluginName(f.Path) && _active.Contains(f.Path.ToLowerInvariant()),
        ConditionFunction.ActiveRegex f => _active.Any(IsMatch(f.Pattern)),
        ConditionFunction.ManyActive f => _active.Where(IsMatch(f.Pattern)).Take(2).Count() == 2,
        ConditionFunction.IsMaster f => _context.Game != LootGame.OpenMW && PluginOnDisk(f.Path) && _context.IsMaster(PluginName(f.Path)),
        ConditionFunction.Checksum f => Checksum(f),
        ConditionFunction.Version f => Compare(ReadVersion(f.Path), f.Comparator, f.Value),
        ConditionFunction.ProductVersion f => Compare(ReadProductVersion(f.Path), f.Comparator, f.Value),
        ConditionFunction.FilenameVersion f => FilenameVersion(f),
        ConditionFunction.DescriptionContains f =>
            PluginOnDisk(f.Path) && _context.PluginDescription(PluginName(f.Path)) is { } description && Pattern(f.Pattern).IsMatch(description),
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };

    /// <summary>The file a condition path names, or null (after a log line, once per path) when the path leaves the game folder.</summary>
    private string? Resolve(string path)
    {
        var resolved = ConditionPaths.Resolve(_context.DataDirectory, path, ConditionPaths.ShouldTryGhost(_context.Game, path));
        if (resolved is null) Refuse(path, "is rooted or leaves the game folder");
        return resolved;
    }

    /// <summary>Plugin lookups take a bare file name, never a path.</summary>
    private bool IsPluginName(string path)
    {
        if (ConditionPaths.IsPlainFileName(path)) return true;

        Refuse(path, "is not a plain plugin file name");
        return false;
    }

    private void Refuse(string path, string reason)
    {
        if (_refused.Add(path)) _logger?.LogInformation("Condition path \"{Path}\" {Reason}; the condition does not hold", path, reason);
    }

    /// <summary>The regular file a condition path names, open for reading, or null when it is missing, refused, or a pipe, device or the like.</summary>
    private FileStream? OpenRegularFile(string path) =>
        Resolve(path) is { } resolved ? ConditionPaths.OpenRegularFile(resolved) : null;

    /// <summary>The name the context knows a plugin by: the path as written, without a <c>.ghost</c> suffix.</summary>
    private string PluginName(string path) => ConditionPaths.NormaliseFileName(_context.Game, path);

    /// <summary>
    /// Like the interpreter, a plugin that is not on disk has no master flag, description or version, whatever the
    /// context knows about it from the loadout.
    /// </summary>
    private bool PluginOnDisk(string path) => IsPluginName(path) && Resolve(path) is { } resolved && Exists(resolved);

    private IReadOnlyList<string> Entries(string parent) =>
        ConditionPaths.ListDirectory(_context.DataDirectory, parent, _context.Game);

    private Func<string, bool> IsMatch(string pattern)
    {
        var regex = Pattern(pattern);
        return regex.IsMatch;
    }

    private Regex Pattern(string pattern)
    {
        if (!_patterns.TryGetValue(pattern, out var regex))
            _patterns[pattern] = regex = new Regex(pattern, ConditionRegex.Options);

        return regex;
    }

    private static bool Exists(string path) => System.IO.File.Exists(path) || Directory.Exists(path);

    private bool FileSize(ConditionFunction.FileSize function)
    {
        // The size of the file a deployed symbolic link ends at, taken without needing to read it (as libloot does).
        return Resolve(function.Path) is { } resolved
               && ConditionPaths.SizeOfRegularFile(resolved) is { } size
               && size == (long)function.Size;
    }

    private bool IsReadable(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                entries.MoveNext();
                return true;
            }

            using var stream = ConditionPaths.OpenRegularFile(path);
            return stream is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsExecutable(ConditionFunction.IsExecutable function)
    {
        return ReadFile(function.Path, PeFileVersion.IsPortableExecutable);
    }

    private bool Checksum(ConditionFunction.Checksum function)
    {
        if (Resolve(function.Path) is not { } path) return false;

        var key = path.ToLowerInvariant();
        if (_checksums.TryGetValue(key, out var known)) return known == function.Crc;

        var crc = TryRead(function.Path, () =>
        {
            using var stream = ConditionPaths.OpenRegularFile(path);
            return stream is null ? (uint?)null : Crc32OfStream(stream);
        });
        if (crc is null) return false;

        _checksums[key] = crc.Value;
        return crc == function.Crc;
    }

    // The app's block size and read-ahead hint; a stream handed to the hasher reads in small blocks.
    private static uint Crc32OfStream(FileStream stream)
    {
        SequentialFileRead.AdviseSequential(stream);

        var hash = new Crc32();
        var buffer = new byte[HashingLimits.ReadBlockSize];
        int read;
        while ((read = stream.Read(buffer)) > 0) hash.Append(buffer.AsSpan(0, read));

        return hash.GetCurrentHashAsUInt32();
    }

    /// <summary>The version of a plugin from its description, or of any other file from its PE version resource. Null when there is none.</summary>
    private LootVersion? ReadVersion(string path)
    {
        if (ConditionPaths.HasPluginExtension(_context.Game, path))
        {
            // Like the interpreter, a plugin that is not on disk has no version, whatever the context knows about it.
            if (!PluginOnDisk(path)) return null;

            return PluginDescriptionVersion.Extract(_context.PluginDescription(PluginName(path))) is { } version
                ? LootVersion.Parse(version)
                : null;
        }

        return ReadFile(path, PeFileVersion.Read) is { } text ? LootVersion.Parse(text) : null;
    }

    private LootVersion? ReadProductVersion(string path)
    {
        return ReadFile(path, PeFileVersion.ReadProductVersion) is { } text ? LootVersion.Parse(text) : null;
    }

    /// <summary>
    /// Reads a file's version data. A file that is missing, cannot be opened or fails mid-read yields the default,
    /// which a version condition treats as not holding rather than as an error.
    /// </summary>
    private T? ReadFile<T>(string conditionPath, Func<Stream, T?> read)
    {
        try
        {
            using var stream = OpenRegularFile(conditionPath);
            return stream is null ? default : read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static bool Compare(LootVersion? actual, ComparisonOperator op, string given) =>
        actual is not null && actual.Compare(op, LootVersion.Parse(given));

    private bool FilenameVersion(ConditionFunction.FilenameVersion function)
    {
        var regex = Pattern(function.Pattern);
        var given = LootVersion.Parse(function.Value);
        foreach (var name in Entries(function.ParentPath))
        {
            var group = regex.Match(name).Groups[1];
            if (group.Success && LootVersion.Parse(group.Value).Compare(function.Comparator, given)) return true;
        }

        return false;
    }

    private static T TryRead<T>(string fileName, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConditionEvaluationException(fileName, e);
        }
    }
}
