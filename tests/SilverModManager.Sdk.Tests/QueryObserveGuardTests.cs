using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// HyperDuck's <c>Query&lt;T&gt;.Observe</c> runs the query before <c>Subscribe</c> returns, which froze the window
/// for seconds when view models subscribed while activating. Every live query goes through
/// <c>ObserveInBackground</c> instead, so a new direct call has to fail here.
/// </summary>
/// <remarks>
/// The scan covers first-party code only: <see cref="RepoScanGuard"/> skips the vendored MnemonicDB folders, so a direct
/// call inside <c>src/SilverModManager.MnemonicDB*</c> is not caught. Nothing there subscribes while a view activates.
/// </remarks>
public partial class QueryObserveGuardTests
{
    // The model streams take a connection and an id; only a query takes a key selector. That is a lambda (plain, static,
    // parenthesised, over a line break) or a method group, with or without type arguments (which may nest, as in
    // SortItemKey<string>). A one-argument call on a receiver whose name ends in `checker` is the update checker's own
    // Observe(file), which is not a query. The method-group branch is deliberately wide: it also flags any other
    // single-argument .Observe(name), for example watcher.Observe(path), so such a call has to be renamed or reworked.
    // ObserveInto evaluates the query before it returns just as Observe does, so it is banned outright.
    [GeneratedRegex(@"(?<![Cc]hecker)\.Observe(Into)?\s*(<(?:[^<>]|<[^<>]*>)*>)?\s*\(\s*(static\s+)?(\(?[\w, ]+\)?\s*=>|\w+\s*\))")]
    private static partial Regex DirectQueryObserve();

    private static IEnumerable<int> LinesOfDirectObserves(string source) =>
        DirectQueryObserve().Matches(source).Select(m => source.Take(m.Index).Count(c => c == '\n') + 1);

    [Theory]
    [InlineData("query.Observe(row => row.Id)")]
    [InlineData("query.Observe((row) => row.Id)")]
    [InlineData("query.Observe(static row => row.Id)")]
    [InlineData("query.Observe(static (row) => row.Id)")]
    [InlineData("query.Observe<EntityId>(row => row.Id)")]
    [InlineData("query.Observe<(int A, int B)>(row => row)")]
    [InlineData("query.Observe(KeyOf)")]
    [InlineData("query.Observe<SortItemKey<string>>(row => row.Key)")]
    [InlineData("query.Observe<Dictionary<string, int>>(row => row.Key)")]
    [InlineData("query.ObserveInto(cache, row => row.Id)")]
    [InlineData("query\n    .Observe(\n        row => row.Id)")]
    public void Test_TheGuardRecognisesEveryShapeOfDirectCall(string source)
    {
        LinesOfDirectObserves(source).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("Model.Observe(connection, id)")]
    [InlineData("checker.Observe(file).Select(x => x)")]
    [InlineData("_checker.Observe(file)")]
    [InlineData("updateChecker.Observe(file)")]
    [InlineData("query.ObserveInBackground(row => row.Id)")]
    public void Test_TheGuardLeavesOtherCallsAlone(string source)
    {
        LinesOfDirectObserves(source).Should().BeEmpty();
    }

    [Fact]
    public void Test_NoLiveQueryIsObservedInsideSubscribe()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs");
        var found = files.SelectMany(file => LinesOfDirectObserves(File.ReadAllText(Path.Combine(root, file))).Select(line => $"{file}:{line}"));
        string.Join(Environment.NewLine, found)
            .Should().BeEmpty("call ObserveInBackground so the first evaluation leaves the subscribing thread");
    }
}
