using System.Collections.Concurrent;
using System.Net;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// Keeps the thumbnail requests of a card grid polite. A grid asks for dozens of images at once, and image hosts such
/// as imgur answer a burst with 429. This handler caps the requests in flight and, once a host answers 429, holds
/// back every later request to that host until its <c>Retry-After</c> has passed. A request is never retried here:
/// the 429 itself goes back to the caller, and the card keeps its placeholder until it asks again.
/// </summary>
public sealed class ThumbnailThrottleHandler : DelegatingHandler
{
    /// <summary>How long a host is left alone after a 429 that names no <c>Retry-After</c>.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(30);

    /// <summary>A pause longer than this fails requests at once instead of tying up the queue behind them.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);

    public ThumbnailThrottleHandler(HttpMessageHandler inner, int maxConcurrency = 4, TimeProvider? time = null) : base(inner)
    {
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _time = time ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";

        await _slots.WaitAsync(cancellationToken);
        try
        {
            await WaitOutPauseAsync(host, cancellationToken);

            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) PauseHost(host, response);
            return response;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task WaitOutPauseAsync(string host, CancellationToken cancellationToken)
    {
        while (_pausedUntil.TryGetValue(host, out var until))
        {
            var remaining = until - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;
            if (remaining > LongestWait) throw new HttpRequestException($"{host} asked to be left alone for {remaining.TotalSeconds:F0}s", inner: null, HttpStatusCode.TooManyRequests);

            await Task.Delay(remaining, _time, cancellationToken);
        }
    }

    private void PauseHost(string host, HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var pause = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - _time.GetUtcNow() : DefaultPause);
        if (pause < TimeSpan.Zero) pause = TimeSpan.Zero;

        var until = _time.GetUtcNow() + pause;
        _pausedUntil.AddOrUpdate(host, until, (_, current) => current > until ? current : until);
    }
}
