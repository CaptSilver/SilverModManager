using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// The syntactic ways code stalls the window or the thread pool: waiting on a task, invoking the
/// dispatcher synchronously, and letting a <c>Parallel</c> loop start one worker per core. The scans
/// walk Roslyn syntax, not text, so a property that happens to be called <c>Result</c> is not a hit.
/// Allowlist entries are keyed by file and the trimmed first line of the call, like the other repo scans.
/// </summary>
public class UiThreadBlockingGuardTests
{
    private const string UiProject = "src/SilverModManager.App.UI";

    /// <summary>
    /// (file, trimmed first line) of every node <paramref name="select"/> picks out of the files'
    /// syntax trees. The line is that of the node's first token, matching <see cref="RepoScanGuard.AssertMatchesAllowlist"/>.
    /// </summary>
    internal static HashSet<(string File, string Line)> Scan(string root, IEnumerable<string> files, Func<SyntaxNode, bool> isHit)
    {
        var found = new HashSet<(string File, string Line)>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            var tree = CSharpSyntaxTree.ParseText(text);
            foreach (var node in tree.GetRoot().DescendantNodes().Where(isHit))
            {
                var line = tree.GetLineSpan(node.Span).StartLinePosition.Line;
                found.Add((file, text.Split('\n')[line].Trim()));
            }
        }

        return found;
    }

    /// <summary>.GetAwaiter().GetResult(), a zero-argument .Wait(), or .Result on a task-shaped receiver.</summary>
    internal static bool IsSyncOverAsync(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call)
        {
            var name = member.Name.Identifier.ValueText;
            if (name == "Wait" && call.ArgumentList.Arguments.Count == 0) return true;
            if (name == "GetResult" && member.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } && inner.Name.Identifier.ValueText == "GetAwaiter") return true;
        }

        if (node is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } access)
        {
            return access.Expression switch
            {
                InvocationExpressionSyntax => true,
                IdentifierNameSyntax id => IsTaskName(id.Identifier.ValueText),
                MemberAccessExpressionSyntax inner => IsTaskName(inner.Name.Identifier.ValueText),
                _ => false,
            };
        }

        return false;

        static bool IsTaskName(string name) => name.EndsWith("Task", StringComparison.Ordinal) || name.EndsWith("Async", StringComparison.Ordinal);
    }

    internal static bool IsSynchronousDispatcherInvoke(SyntaxNode node)
        => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
           && member.Name.Identifier.ValueText == "Invoke"
           && member.Expression.ToString().Replace(" ", "") == "Dispatcher.UIThread";

    /// <summary>
    /// A Parallel.ForEach / ForEachAsync / ForAsync call is bounded when one of its arguments is a
    /// ParallelOptions that sets MaxDegreeOfParallelism, written inline or held in a variable of the file.
    /// An options object passed only for its token is unbounded.
    /// </summary>
    internal static bool IsUnboundedParallelLoop(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Parallel" } } member } call) return false;
        if (member.Name.Identifier.ValueText is not ("ForEach" or "ForEachAsync" or "ForAsync")) return false;

        var root = node.SyntaxTree.GetRoot();
        return !call.ArgumentList.Arguments.Any(argument => SetsMaxDegree(argument.Expression, root));

        static bool SetsMaxDegree(ExpressionSyntax expression, SyntaxNode root)
        {
            if (expression is ObjectCreationExpressionSyntax created)
                return created.Type.ToString() == "ParallelOptions" && InitializerSetsMaxDegree(created.Initializer);

            if (expression is not IdentifierNameSyntax id) return false;
            var name = id.Identifier.ValueText;
            var declared = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Any(v => v.Identifier.ValueText == name && v.Initializer?.Value is ObjectCreationExpressionSyntax { } c && InitializerSetsMaxDegree(c.Initializer));
            var assigned = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target, Name.Identifier.ValueText: "MaxDegreeOfParallelism" } && target.Identifier.ValueText == name);
            return declared || assigned;
        }

        static bool InitializerSetsMaxDegree(InitializerExpressionSyntax? initializer)
            => initializer is not null && initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "MaxDegreeOfParallelism" });
    }

    [Fact]
    public void AppUi_HasNoSyncOverAsync()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var found = Scan(root, RepoScanGuard.EnumerateFiles(root, UiProject, "*.cs"), IsSyncOverAsync);

        RepoScanGuard.AssertMatchesAllowlist(found,
        [
            ("src/SilverModManager.App.UI/Pages/About/AboutPageViewModel.cs",
                "using var stream = new EmbeddedResourceStreamFactory<AboutPageViewModel>(name).GetStreamAsync().GetAwaiter().GetResult();",
                "reads an embedded resource; no IO wait"),
            ("src/SilverModManager.App.UI/Pages/Changelog/ChangelogPageViewModel.cs",
                "using var stream = new EmbeddedResourceStreamFactory<ChangelogPageViewModel>(ChangelogResourceName).GetStreamAsync().GetAwaiter().GetResult();",
                "reads an embedded resource; no IO wait"),
        ], "waiting on a task from the UI project blocks the window; await it on the pool instead");
    }

    [Fact]
    public void AppUi_HasNoSynchronousDispatcherInvoke()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var found = Scan(root, RepoScanGuard.EnumerateFiles(root, UiProject, "*.cs"), IsSynchronousDispatcherInvoke);

        RepoScanGuard.AssertMatchesAllowlist(found,
        [
            ("src/SilverModManager.App.UI/Overlays/OverlayController.cs", "Dispatcher.UIThread.Invoke(() =>", "raises one property-changed notification, no IO inside the call"),
            ("src/SilverModManager.App.UI/Pages/MyGames/MyGamesViewModel.cs", "Dispatcher.UIThread.Invoke(() =>", "navigates after the loadout lookup has already run on the pool"),
            ("src/SilverModManager.App.UI/Pages/MyLoadouts/GameLoadoutsSectionEntry/GameLoadoutsSectionEntryViewModel.cs", "Dispatcher.UIThread.Invoke(() =>", "navigates with a loadout id that is already loaded, no IO inside the call"),
        ], "Dispatcher.UIThread.Invoke blocks the caller until the UI thread is free; use Post or InvokeAsync");
    }

    [Fact]
    public void Src_ParallelLoopsAreBounded()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var found = Scan(root, RepoScanGuard.EnumerateFiles(root, "src", "*.cs"), IsUnboundedParallelLoop);

        RepoScanGuard.AssertMatchesAllowlist(found,
        [
            ("src/SilverModManager.Abstractions.Loadouts.Synchronizers/Deployment/CopyDeploymentBackend.cs",
                "await Parallel.ForEachAsync(backupNodes, async (item, _) =>",
                "asks the store whether it has each hash and builds archive entries; the bytes are streamed later by BackupFiles"),
            ("src/SilverModManager.App.GarbageCollection.DataModel/DataStoreNxArchiveFinder.cs",
                "Parallel.ForEach(allFiles, nxArchivePath =>",
                "parses each archive's header, a few kilobytes at the start, not the file's contents"),
            ("src/SilverModManager.DataModel/NxFileStore.cs",
                "Parallel.ForEach(files, file =>",
                "creates directories and empty files and looks up which archive holds each hash; no contents are read"),
            ("src/SilverModManager.DataModel/NxFileStore.cs",
                "Parallel.ForEach(filesArr, hash =>",
                "looks up which archive holds each hash in memory; no contents are read"),
            ("src/SilverModManager.DataModel/Diagnostics/DiagnosticManager.cs",
                "await Parallel.ForEachAsync(diagnosticEmitters.OfType<ILoadoutDiagnosticEmitter>(), cancellationToken, async (emitter, token) =>",
                "runs each emitter over the in-memory sync tree; one iteration per emitter, none streams file contents"),
            ("src/SilverModManager.DataModel/Sorting/Sorter.cs",
                "Parallel.ForEach(partitioner, tuple =>",
                "CPU-bound rule refinement over in-memory items, already split into range partitions"),
            ("src/SilverModManager.Networking.GOG/CLI/Verbs.cs",
                "await Parallel.ForEachAsync(depotItems, token, async (depot, token) =>",
                "fetches one depot manifest per item over the network; no file contents"),
            ("src/SilverModManager.Networking.GOG/CLI/Verbs.cs",
                "await Parallel.ForEachAsync(hashFiles, token, async (file, token) =>",
                "deserialises the small per-file hash index JSON files, not game files"),
            ("src/SilverModManager.Networking.Steam/CLI/Verbs.cs",
                "await Parallel.ForEachAsync(hashFiles, token, async (file, token) =>",
                "deserialises the small per-file hash index JSON files, not game files"),
        ], "a Parallel loop without MaxDegreeOfParallelism starts a worker per core; cap it, with HashingLimits.FileReaders when it streams file contents");
    }
}
