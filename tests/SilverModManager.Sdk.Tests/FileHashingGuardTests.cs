using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// A file hashed through a stream with a small buffer moves about 107 MB/s on the NVMe, against 2.6 GB/s for 1 MB
/// reads. <c>FileHashing</c> in <c>src/SilverModManager.Sdk/Hashes</c> opens files for sequential reads and hashes them in
/// 1 MB blocks, so src hashes a file through it or not at all. This scan fails on any other call that feeds a stream or a
/// path to a hasher, unless it sits inside the callback of <c>FileHashing.Read</c> (whose stream is the sequential one)
/// or is listed with the reason its input is not a file on disk.
/// </summary>
public class FileHashingGuardTests
{
    private const string HashesFolder = "src/SilverModManager.Sdk/Hashes/";

    private static readonly string[] AlwaysFileHashing = ["XxHash3Async", "HashStream", "ComputeHash", "ComputeHashAsync", "HashDataAsync"];

    /// <summary>A hasher call that reads a stream or a path: the entry points the shared helper wraps.</summary>
    internal static bool IsStreamHashCall(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call) return false;
        var name = member.Name.Identifier.ValueText;
        if (AlwaysFileHashing.Contains(name)) return true;

        var receiver = member.Expression.ToString();
        if (name == "HashAsync") return receiver.EndsWith("Hasher", StringComparison.Ordinal) || receiver == "MinimalHash";
        if (name == "HashData") return call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Contains("tream", StringComparison.OrdinalIgnoreCase) == true;
        return false;
    }

    /// <summary>True for a node inside a lambda that is an argument of <c>FileHashing.Read</c>.</summary>
    internal static bool IsInsideFileHashingRead(SyntaxNode node)
        => node.Ancestors().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "FileHashing" }, Name.Identifier.ValueText: "Read" }
            && call.ArgumentList.Arguments.Any(argument => argument.Expression is LambdaExpressionSyntax lambda && lambda.Span.Contains(node.Span)));

    [Fact]
    public void Src_HashesFilesOnlyThroughFileHashing()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").Where(file => !file.StartsWith(HashesFolder, StringComparison.Ordinal));

        var found = UiThreadBlockingGuardTests.Scan(root, files, node => IsStreamHashCall(node) && !IsInsideFileHashingRead(node));

        RepoScanGuard.AssertMatchesAllowlist(found,
        [
            ("src/SilverModManager.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs", "var hash = await Xx3Hasher.HashAsync(rendered);", "hashes the rendered bytes in a MemoryStream, no file read"),
            ("src/SilverModManager.Abstractions.Loadouts.Synchronizers/Deployment/CopyDeploymentBackend.cs", "var hash = await Xx3Hasher.HashAsync(rendered);", "hashes the rendered bytes in a MemoryStream, no file read"),
            ("src/SilverModManager.Abstractions.Games.Merge/MergeEngine.cs", "hashes = MultiHasher.HashStream(new MemoryStream(bytes)).GetAwaiter().GetResult();", "hashes bytes already read into memory, in 1 MB copies, by the lines above"),
            ("src/SilverModManager.Backend/Games/GameLocationsService.cs", "return await MinimalHash.HashAsync<Hash, XxHash3, Xx3Hasher>(fileStream, cancellationToken: cancellationToken);", "the minimal hash is three sampled 64 KB blocks by definition; a sequential hint would make the kernel read ahead of blocks it never uses"),
            ("src/SilverModManager.Collections/InstallCollectionDownloadJob.cs", "var md5 = await Md5Hasher.HashAsync(stream, cancellationToken: token);", "stream comes from the file store, not a file on disk"),
            ("src/SilverModManager.Collections/InstallCollectionDownloadJob.cs", "var originalFileHashes = await MultiHasher.HashStream(inputStream, cancellationToken: cancellationToken);", "stream comes from the file store, not a file on disk"),
            ("src/SilverModManager.Collections/InstallCollectionDownloadJob.cs", "var patchedFileHashes = await MultiHasher.HashStream(outputStream, cancellationToken: cancellationToken);", "hashes the patch output held in memory"),
            ("src/SilverModManager.Library/LibraryFileDigests.cs", "digest = await Sha256Hasher.HashAsync(stream, cancellationToken: cancellationToken);", "stream comes from the file store, not a file on disk"),
            ("src/SilverModManager.Library/LibraryFileDigests.cs", "digest = await Xx64Hasher.HashAsync(stream, cancellationToken: cancellationToken);", "stream comes from the file store, not a file on disk"),
            ("src/SilverModManager.Wabbajack/Acquire/GameFileSourceLookup.cs", "var hashes = await Hasher.HashAsync(Counting(stored, onRead), cancellationToken: cancellationToken);", "stream comes from the file store, wrapped in a read counter"),
            ("src/SilverModManager.Wabbajack/Acquire/GameFileSourceLookup.cs", "var both = await Hasher.HashAsync(Counting(stream, onRead), cancellationToken: cancellationToken);", "stream is opened with SequentialFileRead, wrapped in a read counter"),
            ("src/SilverModManager.Networking.GOG/CLI/Verbs.cs", "var multiHash = await MultiHasher.HashStream(stream, cancellationToken: token);", "stream is a download from the GOG depot, not a file on disk"),
            ("src/SilverModManager.Networking.Steam/CLI/Verbs.cs", "var multiHash = await MultiHasher.HashStream(stream, cancellationToken: token);", "stream is a download from the Steam depot, not a file on disk"),
        ], "hash files with FileHashing (1 MB sequential reads); a stream or path handed to a hasher directly reads in small blocks");
    }

    [Theory]
    [InlineData("class C { async Task M(AbsolutePath p) { var h = await p.XxHash3Async(); } }", true)]
    [InlineData("class C { async Task M(Stream s) { var h = await MultiHasher.HashStream(s); } }", true)]
    [InlineData("class C { async Task M(Stream s) { var h = await Sha256Hasher.HashAsync(s); } }", true)]
    [InlineData("class C { async Task M(Stream s) { var h = await MinimalHash.HashAsync<Hash, XxHash3, Xx3Hasher>(s); } }", true)]
    [InlineData("class C { async Task M(Stream stream) { var h = await MD5.Create().ComputeHashAsync(stream); } }", true)]
    [InlineData("class C { async Task M(Stream fileStream) { var h = await MD5.HashDataAsync(fileStream); } }", true)]
    [InlineData("class C { void M(Stream fileStream) { var h = SHA1.HashData(fileStream); } }", true)]
    [InlineData("class C { void M(byte[] blob) { var h = SHA1.HashData(blob); } }", false)]
    [InlineData("class C { async Task M(AbsolutePath p) { var h = await FileHashing.XxHash3(p); } }", false)]
    public void TheScan_RecognisesTheDirectHashingCalls(string source, bool expected)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);

        tree.GetRoot().DescendantNodes().Any(IsStreamHashCall).Should().Be(expected);
    }

    [Fact]
    public void TheScan_SparesTheCallbackOfFileHashingRead()
    {
        const string source = "class C { async Task M(AbsolutePath p) { await FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(s, cancellationToken: t), default); await Sha256Hasher.HashAsync(null); } }";
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);

        var hits = tree.GetRoot().DescendantNodes().Where(node => IsStreamHashCall(node) && !IsInsideFileHashingRead(node)).ToList();

        hits.Should().ContainSingle().Which.ToString().Should().Contain("HashAsync(null)");
    }
}
