using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// A connection cut in the middle of the body (EOF, reset, a proxy or VPN dropping) is the same situation as a silent
/// one: ask again for the rest, and give up only when asking again brings no new bytes.
/// </summary>
public class HttpDownloadJobDropTests
{
    private static readonly Uri FileUri = new("http://files.test/archive.7z?signature=SECRET");

    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IJobMonitor _monitor;

    public HttpDownloadJobDropTests(IServiceProvider serviceProvider)
    {
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _monitor = serviceProvider.GetRequiredService<IJobMonitor>();
    }

    private static byte[] MakeData(int length)
    {
        var data = new byte[length];
        new Random(7).NextBytes(data);
        return data;
    }

    private static HttpDownloadJob MakeJob(HttpClient client, AbsolutePath destination, int maxRetries = 3, TimeSpan? retryDelay = null) => new()
    {
        Client = client,
        Logger = NullLogger.Instance,
        Uri = FileUri,
        DownloadPageUri = FileUri,
        Destination = destination,
        MaxIdleRetries = maxRetries,
        RetryDelay = retryDelay ?? TimeSpan.Zero,
    };

    private static IOException Eof() => new("Received an unexpected EOF or 0 bytes from the transport stream.");

    public static TheoryData<string> Drops => new() { "eof", "http-response-ended", "socket-reset" };

    private static Exception MakeDrop(string kind) => kind switch
    {
        "eof" => Eof(),
        "http-response-ended" => new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."),
        _ => new IOException("Unable to read data from the transport connection", new SocketException((int)SocketError.ConnectionReset)),
    };

    [Theory(Timeout = 60000)]
    [MemberData(nameof(Drops))]
    public async Task ABodyCutHalfWay_IsResumedWithARangeFromTheBytesWritten(string kind)
    {
        var data = MakeData(300_000);
        var server = new DroppingServer(data, attempt => attempt == 1 ? (100_000, MakeDrop(kind)) : null);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        await _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path));

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().Equal(null, "bytes=100000-300000");
    }

    [Fact(Timeout = 60000)]
    public async Task AServerWithoutRanges_RestartsFromByteZeroAfterADrop()
    {
        var data = MakeData(300_000);
        var server = new DroppingServer(data, attempt => attempt == 1 ? (100_000, Eof()) : null, acceptRanges: false);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        await _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path));

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().Equal(null, null);
    }

    [Fact(Timeout = 60000)]
    public async Task DropsThatEachMakeProgress_NeverUseUpTheLimit()
    {
        var data = MakeData(600_000);
        // Six drops, each after 80 000 new bytes: more drops than the limit, never limit-many in a row without progress
        var server = new DroppingServer(data, attempt => attempt <= 6 ? (80_000, Eof()) : null);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        await _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, maxRetries: 2));

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().HaveCount(7);
    }

    [Fact(Timeout = 60000)]
    public async Task DropsWithoutProgress_FailWithAPlainMessageAfterTheLimit()
    {
        var data = MakeData(300_000);
        // The first attempt gets some bytes, every later one is cut before sending any
        var server = new DroppingServer(data, attempt => (attempt == 1 ? 100_000 : 0, Eof()));
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, maxRetries: 2));
        var thrown = await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<UserFacingException>();

        thrown.Which.Message.Should().Be("files.test kept dropping the connection; downloaded 100 KB of 300 KB");
        thrown.Which.Message.Should().NotContain("SECRET");
        // The first request (which made progress), then two asks that got nothing
        server.GetRanges.Should().HaveCount(3);
        task.Job.Status.Should().Be(JobStatus.Failed);
    }

    [Fact]
    public void TheWaitBeforeAskingAgain_DoublesWithEachAttemptWithoutProgressAndIsCapped()
    {
        var delay = TimeSpan.FromSeconds(2);

        HttpDownloadJob.ResumeDelay(delay, 1).Should().Be(TimeSpan.FromSeconds(2));
        HttpDownloadJob.ResumeDelay(delay, 2).Should().Be(TimeSpan.FromSeconds(4));
        HttpDownloadJob.ResumeDelay(delay, 3).Should().Be(TimeSpan.FromSeconds(8));
        HttpDownloadJob.ResumeDelay(delay, 40).Should().Be(TimeSpan.FromSeconds(30));
        HttpDownloadJob.ResumeDelay(TimeSpan.Zero, 5).Should().Be(TimeSpan.Zero);
    }

    [Fact(Timeout = 60000)]
    public async Task TheUserCancellingWhileTheJobIsDroppingOrWaitingToResume_IsACancel()
    {
        var server = new DroppingServer(MakeData(300_000), _ => (100_000, Eof()));
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        // An hour of waiting between attempts: only the cancel can end this
        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, retryDelay: TimeSpan.FromHours(1)));
        await server.FirstRequest.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _monitor.Cancel(task);

        await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<OperationCanceledException>();
        task.Job.Status.Should().Be(JobStatus.Cancelled);
        server.GetRanges.Should().HaveCount(1);
    }

    /// <summary>
    /// Answers HEAD and GET for one file. For each GET, <paramref name="cut"/> gives how many bytes the body delivers
    /// before it throws the given exception (null: the whole rest of the file).
    /// </summary>
    private sealed class DroppingServer(byte[] data, Func<int, (int After, Exception Error)?> cut, bool acceptRanges = true) : HttpMessageHandler
    {
        private readonly List<string?> _getRanges = [];
        private readonly TaskCompletionSource _firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string?> GetRanges
        {
            get { lock (_getRanges) return _getRanges.ToArray(); }
        }

        public Task FirstRequest => _firstRequest.Task;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = data.Length;
                if (acceptRanges) head.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(head);
            }

            int attempt;
            lock (_getRanges)
            {
                _getRanges.Add(request.Headers.Range?.ToString());
                attempt = _getRanges.Count;
            }
            _firstRequest.TrySetResult();

            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            var rest = data.Length - from;
            var plan = cut(attempt);
            var stream = new CutStream(data.AsMemory(from, rest), plan?.After, plan?.Error);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = rest;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class CutStream(ReadOnlyMemory<byte> body, int? cutAfter, Exception? error) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var limit = cutAfter ?? body.Length;
            if (_position < limit)
            {
                var count = Math.Min(Math.Min(buffer.Length, 16 * 1024), limit - _position);
                body.Slice(_position, count).CopyTo(buffer);
                _position += count;
                return ValueTask.FromResult(count);
            }
            if (_position >= body.Length) return ValueTask.FromResult(0);
            throw error ?? new InvalidOperationException("no error planned");
        }
    }
}
