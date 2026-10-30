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
        if (name == "HashAsync") return receiver.EndsWith("Hasher", StringComparison.Ordinal) || receiver.EndsWith("Value", StringComparison.Ordinal) || receiver.StartsWith("StreamingHasher<", StringComparison.Ordinal) || receiver.StartsWith("MinimalHash", StringComparison.Ordinal);
        // A hand-written loop over a hasher's state, or a System.IO.Hashing algorithm fed a whole stream.
        if (name == "Initialize" && call.ArgumentList.Arguments.Count == 0) return receiver.EndsWith("Hasher", StringComparison.Ordinal);
        if (name == "Update") return receiver.EndsWith("Hasher", StringComparison.Ordinal);
        if (name is "Append" or "AppendAsync") return call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Contains("tream", StringComparison.OrdinalIgnoreCase) == true;
        if (name == "HashData") return call.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString().Contains("tream", StringComparison.OrdinalIgnoreCase) == true;
        return false;
    }

    /// <summary>True for a node inside a lambda that is an argument of <c>FileHashing.Read</c>.</summary>
    internal static bool IsInsideFileHashingRead(SyntaxNode node)
        => node.Ancestors().OfType<InvocationExpressionSyntax>().Any(call =>
            call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "FileHashing" }, Name.Identifier.ValueText: "Read" }
            && call.ArgumentList.Arguments.Any(argument => argument.Expression is LambdaExpressionSyntax lambda && lambda.Span.Contains(node.Span)));

    /// <summary>
    /// Inside a <c>FileHashing.Read</c> callback the stream is already sequential, so the ways left to read it slowly are
    /// an explicit buffer size handed to a hasher and a stream layered over it with a small buffer.
    /// </summary>
    internal static bool IsSmallBufferBypass(SyntaxNode node)
    {
        if (node is ObjectCreationExpressionSyntax creation) return creation.Type.ToString() is "BufferedStream" or "FileStream" or "System.IO.BufferedStream" or "System.IO.FileStream";
        if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call) return false;
        if (member.Name.Identifier.ValueText is not ("HashAsync" or "HashStream" or "ComputeHashAsync")) return false;
        var arguments = call.ArgumentList.Arguments;
        if (arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "bufferSize")) return true;
        // HashAsync(stream, bufferSize, token): a second positional argument is the buffer size.
        return member.Name.Identifier.ValueText == "HashAsync" && arguments.Count >= 2 && arguments[1].NameColon is null && arguments[1].Expression is not IdentifierNameSyntax { Identifier.ValueText: "token" or "cancellationToken" or "ct" or "t" };
    }

    [Fact]
    public void Src_ReadCallbacksKeepTheSequentialStream()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").Where(file => !file.StartsWith(HashesFolder, StringComparison.Ordinal));

        var found = UiThreadBlockingGuardTests.Scan(root, files, node => IsInsideFileHashingRead(node) && IsSmallBufferBypass(node));

        RepoScanGuard.AssertMatchesAllowlist(found, [], "a FileHashing.Read callback hashes the stream it is given with the default 1 MB blocks; do not pass a buffer size or wrap it in a BufferedStream/FileStream");
    }

    [Fact]
    public void Src_OnlyTheHashesFolderAndTestsOpenFilesForFileHashingRead()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").Where(file => !file.StartsWith(HashesFolder, StringComparison.Ordinal));

        var found = UiThreadBlockingGuardTests.Scan(root, files, IsOpenerOverloadCall);

        RepoScanGuard.AssertMatchesAllowlist(found,
        [
            ("src/SilverModManager.Games.FileHashes/LocalVanillaBaseline.cs", "var hash = await FileHashing.Read(_fileSystem.FromUnsanitizedFullPath(candidate.DiskPath), _openRead, async (stream, readToken) => await MultiHasher.HashStream(stream, readToken), token);", "the opener is the test seam of the baseline hasher and defaults to SequentialFileRead.Open"),
        ], "FileHashing.Read opens the file sequentially; only a test seam may pass its own opener");
    }

    /// <summary>A <c>FileHashing.Read</c> call whose third argument is the hashing lambda: the overload that takes an opener (file, open, hash).</summary>
    internal static bool IsOpenerOverloadCall(SyntaxNode node)
        => node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "FileHashing" }, Name.Identifier.ValueText: "Read" } } call
           && call.ArgumentList.Arguments.Count >= 3 && call.ArgumentList.Arguments[2].Expression is LambdaExpressionSyntax;

    [Theory]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(s, 4096, t), default); } }", true)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(s, bufferSize: 4096), default); } }", true)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(new BufferedStream(s, 8192), cancellationToken: t), default); } }", true)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(new FileStream(\"x\", FileMode.Open)), default); } }", true)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(s, cancellationToken: t), default); } }", false)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => Sha256Hasher.HashAsync(s, t), default); } }", false)]
    public void TheScan_RejectsSmallBuffersInsideTheReadCallback(string source, bool expected)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);

        tree.GetRoot().DescendantNodes().Any(node => IsInsideFileHashingRead(node) && IsSmallBufferBypass(node)).Should().Be(expected);
    }

    [Theory]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, path => File.OpenRead(path.ToString()), (s, t) => default, default); } }", true)]
    [InlineData("class C { void M(AbsolutePath p) { FileHashing.Read(p, (s, t) => default, default); } }", false)]
    public void TheScan_RecognisesTheOpenerOverload(string source, bool expected)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);

        tree.GetRoot().DescendantNodes().Any(IsOpenerOverloadCall).Should().Be(expected);
    }

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
            ("src/SilverModManager.Wabbajack/Acquire/GameFileSourceLookup.cs", "var both = await Hasher.HashAsync(Counting(stored, onRead), cancellationToken: cancellationToken);", "stream comes from the file store, wrapped in a read counter"),
            ("src/SilverModManager.Wabbajack/Acquire/GameFileSourceLookup.cs", "var hashes = await MultiHasher.HashStream(Counting(stored, onRead), cancellationToken);", "stream comes from the file store, wrapped in a read counter"),
            ("src/SilverModManager.Wabbajack/Acquire/GameFileSourceLookup.cs", "hashes = await MultiHasher.HashStream(Counting(stream, onRead), cancellationToken);", "stream is opened by OpenLive, which defaults to SequentialFileRead.Open, wrapped in a read counter"),
            ("src/SilverModManager.Wabbajack/Acquire/StreamDigests.cs", "var state = DigestHasher.Initialize();", "hashes a stream the caller supplies in 1 MB blocks; the stream is not always a file"),
            ("src/SilverModManager.Wabbajack/Acquire/StreamDigests.cs", "state = DigestHasher.Update(state, buffer, 0, read);", "hashes a stream the caller supplies in 1 MB blocks; the stream is not always a file"),
            ("src/SilverModManager.Wabbajack/Install/Xx64HashingStream.cs", "private readonly XxHash64 _state = Xx64Hasher.Initialize();", "hashes bytes as they pass through a write or read stream, with the caller's buffer"),
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
    [InlineData("class C { async Task M(Stream s) { var h = await Sha256Value.HashAsync(s); } }", true)]
    [InlineData("class C { async Task M(Stream s, CancellationToken t) { var h = await StreamingHasher<Hash, XxHash3, Xx3Hasher>.HashAsync(s, 4096, t); } }", true)]
    [InlineData("class C { void M() { var s = Xx64Hasher.Initialize(); } }", true)]
    [InlineData("class C { void M(Hash s, byte[] b) { s = DigestHasher.Update(s, b, 0, 1); } }", true)]
    [InlineData("class C { async Task M(XxHash3 h, Stream stream) { await h.AppendAsync(stream); } }", true)]
    [InlineData("class C { void M(XxHash3 h, byte[] b) { h.Append(b); } }", false)]
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
