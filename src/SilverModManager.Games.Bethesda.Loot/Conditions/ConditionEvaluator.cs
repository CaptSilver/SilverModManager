using System.IO.Hashing;
using System.Text.RegularExpressions;
using SilverModManager.Games.Bethesda.Loot.Database;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
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

    public ConditionEvaluator(IConditionContext context)
    {
        _context = context;
        _active = new HashSet<string>(context.ActivePlugins.Select(name => name.ToLowerInvariant()));
    }

    public bool Evaluate(string condition)
    {
        if (!_parsed.TryGetValue(condition, out var expression))
            _parsed[condition] = expression = ConditionExpression.Parse(condition);

        return Evaluate(expression);
    }

    private bool Evaluate(ConditionExpression expression)
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
        ConditionFunction.FilePath f => Exists(Resolve(f.Path)),
        ConditionFunction.FileRegex f => Entries(f.ParentPath).Any(IsMatch(f.Pattern)),
        ConditionFunction.Many f => Entries(f.ParentPath).Where(IsMatch(f.Pattern)).Take(2).Count() == 2,
        ConditionFunction.FileSize f => FileSize(f),
        ConditionFunction.Readable f => IsReadable(Resolve(f.Path)),
        ConditionFunction.IsExecutable f => IsExecutable(f),
        ConditionFunction.ActivePath f => _active.Contains(f.Path.ToLowerInvariant()),
        ConditionFunction.ActiveRegex f => _active.Any(IsMatch(f.Pattern)),
        ConditionFunction.ManyActive f => _active.Where(IsMatch(f.Pattern)).Take(2).Count() == 2,
        ConditionFunction.IsMaster f => _context.Game != LootGame.OpenMW && _context.IsMaster(PluginName(f.Path)),
        ConditionFunction.Checksum f => Checksum(f),
        ConditionFunction.Version f => Compare(ReadVersion(f.Path), f.Comparator, f.Value),
        ConditionFunction.ProductVersion f => Compare(ReadProductVersion(f.Path), f.Comparator, f.Value),
        ConditionFunction.FilenameVersion f => FilenameVersion(f),
        ConditionFunction.DescriptionContains f =>
            _context.PluginDescription(PluginName(f.Path)) is { } description && Pattern(f.Pattern).IsMatch(description),
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };

    private string Resolve(string path) =>
        ConditionPaths.Resolve(_context.DataDirectory, path, ConditionPaths.ShouldTryGhost(_context.Game, path));

    /// <summary>The name the context knows a plugin by: the path as written, without a <c>.ghost</c> suffix.</summary>
    private string PluginName(string path) => ConditionPaths.NormaliseFileName(_context.Game, path);

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
        var path = Resolve(function.Path);
        return System.IO.File.Exists(path) && TryRead(function.Path, () => new FileInfo(path).Length) == (long)function.Size;
    }

    private static bool IsReadable(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                entries.MoveNext();
                return true;
            }

            if (!System.IO.File.Exists(path)) return false;
            using var stream = System.IO.File.OpenRead(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsExecutable(ConditionFunction.IsExecutable function)
    {
        using var stream = OpenForVersion(Resolve(function.Path));
        return stream is not null && PeFileVersion.IsPortableExecutable(stream);
    }

    private bool Checksum(ConditionFunction.Checksum function)
    {
        var path = Resolve(function.Path);
        var key = path.ToLowerInvariant();
        if (_checksums.TryGetValue(key, out var known)) return known == function.Crc;

        if (!System.IO.File.Exists(path)) return false;

        var crc = TryRead(function.Path, () => Crc32OfFile(path));

        _checksums[key] = crc;
        return crc == function.Crc;
    }

    // Sequential 1 MB reads, as the rest of the app hashes files; a stream handed to the hasher reads in small blocks.
    private static uint Crc32OfFile(string path)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0,
        });

        var hash = new Crc32();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0) hash.Append(buffer.AsSpan(0, read));

        return hash.GetCurrentHashAsUInt32();
    }

    /// <summary>The version of a plugin from its description, or of any other file from its PE version resource. Null when there is none.</summary>
    private LootVersion? ReadVersion(string path)
    {
        if (ConditionPaths.HasPluginExtension(_context.Game, path))
        {
            return PluginDescriptionVersion.Extract(_context.PluginDescription(PluginName(path))) is { } version
                ? LootVersion.Parse(version)
                : null;
        }

        using var stream = OpenForVersion(Resolve(path));
        return stream is not null && PeFileVersion.Read(stream) is { } text ? LootVersion.Parse(text) : null;
    }

    private LootVersion? ReadProductVersion(string path)
    {
        using var stream = OpenForVersion(Resolve(path));
        return stream is not null && PeFileVersion.ReadProductVersion(stream) is { } text ? LootVersion.Parse(text) : null;
    }

    /// <summary>
    /// Opens a file to read its version. A file that is missing or cannot be opened has no version, which a
    /// version condition treats as not holding rather than as an error.
    /// </summary>
    private static FileStream? OpenForVersion(string path)
    {
        try
        {
            return System.IO.File.Exists(path) ? System.IO.File.OpenRead(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
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
