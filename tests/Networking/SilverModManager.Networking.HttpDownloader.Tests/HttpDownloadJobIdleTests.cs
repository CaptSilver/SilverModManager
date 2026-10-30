using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// A connection that stays open and sends nothing must not hold a download slot forever: the job notices the silence,
/// asks again for the rest of the file, and gives up with a plain message when asking again changes nothing.
/// </summary>
public class HttpDownloadJobIdleTests
{
    private static readonly Uri FileUri = new("http://files.test/archive.7z?signature=SECRET");

    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IJobMonitor _monitor;

    public HttpDownloadJobIdleTests(IServiceProvider serviceProvider)
    {
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _monitor = serviceProvider.GetRequiredService<IJobMonitor>();
    }

    private static byte[] MakeData(int length)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        return data;
    }

    private HttpDownloadJob MakeJob(HttpClient client, AbsolutePath destination, int maxIdleRetries = 3, TimeSpan? idle = null) => new()
    {
        Client = client,
        Logger = NullLogger.Instance,
        Uri = FileUri,
        DownloadPageUri = FileUri,
        Destination = destination,
        IdleTimeout = idle ?? TimeSpan.FromMilliseconds(150),
        MaxIdleRetries = maxIdleRetries,
    };

    [Fact(Timeout = 60000)]
    public async Task ABodyThatGoesQuietHalfWay_IsResumedWithARangeFromWhereItStopped()
    {
        var data = MakeData(300_000);
        var server = new StallingServer(data, attempt => attempt == 1 ? 100_000 : null);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path));
        await task;

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().Equal(null, "bytes=100000-300000");
    }

    [Fact(Timeout = 60000)]
    public async Task AServerWithoutRanges_IsAskedForTheWholeFileAgainAfterAStall()
    {
        var data = MakeData(300_000);
        var server = new StallingServer(data, attempt => attempt == 1 ? 100_000 : null, acceptRanges: false);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path));
        await task;

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().Equal(null, null);
    }

    [Fact(Timeout = 60000)]
    public async Task ABodyThatNeverSendsAByte_FailsWithAPlainMessageAfterTheRetryLimit()
    {
        var server = new StallingServer(MakeData(300_000), _ => 0);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, maxIdleRetries: 2));
        var thrown = await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<DownloadStalledException>();

        thrown.Which.Message.Should().Be("files.test stopped sending data");
        thrown.Which.Message.Should().NotContain("SECRET");
        // The first request and two more
        server.GetRanges.Should().HaveCount(3);
        task.Job.Status.Should().Be(JobStatus.Failed);
    }

    [Fact(Timeout = 60000)]
    public async Task ProgressBetweenStalls_StartsTheRetryCountOver()
    {
        var data = MakeData(500_000);
        // Stalls four times, each after new bytes: more stalls than the limit, but never limit-many in a row
        var server = new StallingServer(data, attempt => attempt <= 4 ? 100_000 : null);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, maxIdleRetries: 2));
        await task;

        File.ReadAllBytes(destination.Path.ToString()).Should().Equal(data);
        server.GetRanges.Should().HaveCount(5);
    }

    [Fact(Timeout = 60000)]
    public async Task TheUserCancellingDuringAStall_IsACancelNotAStallFailure()
    {
        var server = new StallingServer(MakeData(300_000), _ => 100_000);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, idle: TimeSpan.FromMinutes(5)));
        await server.FirstStall.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _monitor.Cancel(task);

        await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<OperationCanceledException>();
        task.Job.Status.Should().Be(JobStatus.Cancelled);
        server.GetRanges.Should().HaveCount(1);
    }

    [Fact(Timeout = 60000)]
    public async Task PausingDuringAStall_PausesTheJobAndDoesNotCountAsIdle()
    {
        var server = new StallingServer(MakeData(300_000), _ => 100_000);
        using var client = new HttpClient(server);
        await using var destination = _temporaryFileManager.CreateFile();

        var task = _monitor.Begin<HttpDownloadJob, AbsolutePath>(MakeJob(client, destination.Path, idle: TimeSpan.FromMinutes(5)));
        await server.FirstStall.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _monitor.Pause(task);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (task.Job.Status == JobStatus.Running && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        task.Job.Status.Should().Be(JobStatus.Paused);
        _monitor.Cancel(task);
        await FluentActions.Awaiting(async () => await task).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Answers HEAD and GET for one file. For each GET, <paramref name="bytesBeforeStall"/> gives how many bytes the body
    /// delivers before it goes silent (null: the whole rest of the file).
    /// </summary>
    private sealed class StallingServer(byte[] data, Func<int, int?> bytesBeforeStall, bool acceptRanges = true) : HttpMessageHandler
    {
        private readonly List<string?> _getRanges = [];
        private readonly TaskCompletionSource _firstStall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string?> GetRanges
        {
            get { lock (_getRanges) return _getRanges.ToArray(); }
        }

        public Task FirstStall => _firstStall.Task;

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

            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            var rest = data.Length - from;
            var stream = new SilentTailStream(data.AsMemory(from, rest), bytesBeforeStall(attempt), _firstStall);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = rest;
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class SilentTailStream(ReadOnlyMemory<byte> body, int? stallAfter, TaskCompletionSource stalled) : Stream
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var limit = stallAfter ?? body.Length;
            if (_position < limit)
            {
                var count = Math.Min(Math.Min(buffer.Length, 16 * 1024), limit - _position);
                body.Slice(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            if (_position >= body.Length) return 0;

            stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
