using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.App.UI.Controls.Cards;

namespace SilverModManager.UI.Tests.Pages;

public class ThumbnailThrottleHandlerTests
{
    private static readonly Uri Imgur = new("https://i.imgur.com/a.png");
    private static readonly Uri OtherImgur = new("https://i.imgur.com/b.png");
    private static readonly Uri Thunderstore = new("https://gcdn.thunderstore.io/live/repository/icons/a-b-1.0.0.png");

    private sealed class FakeInner : HttpMessageHandler
    {
        private int _inFlight;
        public int MaxInFlight { get; private set; }
        public List<Uri> Requests { get; } = [];
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            var now = Interlocked.Increment(ref _inFlight);
            lock (Requests) MaxInFlight = Math.Max(MaxInFlight, now);
            try { return await Respond(request); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    private static HttpResponseMessage TooManyRequests(TimeSpan? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (retryAfter is not null) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return response;
    }

    private static (HttpClient Client, FakeInner Inner, FakeTimeProvider Time) Build(int maxConcurrency = 4)
    {
        var inner = new FakeInner();
        var time = new FakeTimeProvider();
        var handler = new ThumbnailThrottleHandler(inner, maxConcurrency, time);
        return (new HttpClient(handler), inner, time);
    }

    [Fact]
    public async Task NoMoreRequestsAreInFlightThanTheLimit()
    {
        var (client, inner, _) = Build(maxConcurrency: 2);
        var release = new TaskCompletionSource();
        inner.Respond = async _ => { await release.Task; return new HttpResponseMessage(HttpStatusCode.OK); };

        var sends = Enumerable.Range(0, 8).Select(i => client.GetAsync(new Uri($"https://i.imgur.com/{i}.png"))).ToList();
        await Task.Delay(100);
        inner.MaxInFlight.Should().Be(2);

        release.SetResult();
        await Task.WhenAll(sends);
        inner.Requests.Should().HaveCount(8);
        inner.MaxInFlight.Should().Be(2);
    }

    [Fact]
    public async Task After429_ARequestToTheSameHostWaitsForRetryAfter()
    {
        var (client, inner, time) = Build();
        inner.Respond = _ => Task.FromResult(TooManyRequests(TimeSpan.FromSeconds(20)));
        (await client.GetAsync(Imgur)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        inner.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var next = client.GetAsync(OtherImgur);
        await Task.Delay(100);
        next.IsCompleted.Should().BeFalse("the host asked for a pause");
        inner.Requests.Should().ContainSingle();

        time.Advance(TimeSpan.FromSeconds(21));
        (await next).StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task After429_AnotherHostIsNotHeldBack()
    {
        var (client, inner, _) = Build();
        inner.Respond = r => Task.FromResult(r.RequestUri!.Host == "i.imgur.com" ? TooManyRequests(TimeSpan.FromSeconds(20)) : new HttpResponseMessage(HttpStatusCode.OK));
        await client.GetAsync(Imgur);

        (await client.GetAsync(Thunderstore)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A429WithoutRetryAfter_PausesTheHostForTheDefault()
    {
        var (client, inner, time) = Build();
        inner.Respond = _ => Task.FromResult(TooManyRequests(null));
        await client.GetAsync(Imgur);

        inner.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var next = client.GetAsync(OtherImgur);
        await Task.Delay(100);
        next.IsCompleted.Should().BeFalse();

        time.Advance(ThumbnailThrottleHandler.DefaultPause + TimeSpan.FromSeconds(1));
        (await next).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AHostThatAsksForALongPause_FailsRequestsAtOnceWithoutAskingAgain()
    {
        var (client, inner, _) = Build();
        inner.Respond = _ => Task.FromResult(TooManyRequests(TimeSpan.FromHours(1)));
        await client.GetAsync(Imgur);

        var act = () => client.GetAsync(OtherImgur);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        inner.Requests.Should().ContainSingle("the second request must not reach the host");
    }

    [Fact]
    public async Task ARequestWaitingOutAPause_CanBeCancelled()
    {
        var (client, inner, _) = Build();
        inner.Respond = _ => Task.FromResult(TooManyRequests(TimeSpan.FromSeconds(20)));
        await client.GetAsync(Imgur);
        using var cts = new CancellationTokenSource();

        var next = client.GetAsync(OtherImgur, cts.Token);
        await cts.CancelAsync();

        await next.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ASuccessfulResponse_PassesThroughUntouched()
    {
        var (client, inner, _) = Build();

        (await client.GetAsync(Imgur)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(Imgur)).StatusCode.Should().Be(HttpStatusCode.OK);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task RequestsWaitingOutAPause_DoNotHoldTheSlotsFromOtherHosts()
    {
        var (client, inner, time) = Build(maxConcurrency: 2);
        inner.Respond = r => Task.FromResult(r.RequestUri!.Host == "i.imgur.com" ? TooManyRequests(TimeSpan.FromSeconds(20)) : new HttpResponseMessage(HttpStatusCode.OK));
        await client.GetAsync(Imgur);
        inner.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        var waiting = Enumerable.Range(0, 4).Select(i => client.GetAsync(new Uri($"https://i.imgur.com/w{i}.png"))).ToList();
        await Task.Delay(100);
        waiting.Should().OnlyContain(t => !t.IsCompleted);

        var other = client.GetAsync(Thunderstore);
        var finished = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(other, "requests parked behind a pause must not occupy the concurrency slots");

        time.Advance(TimeSpan.FromSeconds(21));
        (await Task.WhenAll(waiting)).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task APauseJustUnderTheLongestWait_IsWaitedOutRatherThanFailed()
    {
        var (client, inner, time) = Build();
        inner.Respond = _ => Task.FromResult(TooManyRequests(ThumbnailThrottleHandler.LongestWait - TimeSpan.FromSeconds(1)));
        await client.GetAsync(Imgur);

        inner.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        var next = client.GetAsync(OtherImgur);
        await Task.Delay(100);
        next.IsCompleted.Should().BeFalse();

        time.Advance(ThumbnailThrottleHandler.LongestWait);
        (await next).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void TheThumbnailClientTimeout_OutlastsTheLongestPauseItMayWaitOut()
    {
        using var client = SilverModManager.App.UI.ImagePipelines.CreateThumbnailHttpClient();

        client.Timeout.Should().BeGreaterThan(ThumbnailThrottleHandler.LongestWait + TimeSpan.FromSeconds(30));
    }
}
