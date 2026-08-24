using System.Net;
using System.Net.Http;
using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin.Guiding;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Tests for 429 retry/backoff and Retry-After parsing logic.
/// Covers:
///   - Retry-After header parsing (integer seconds, HTTP-date, absent/malformed)
///   - 429 is NOT treated as a permanent failure
///   - 429 delay uses Retry-After when present; falls back to exponential back-off
///   - After max retries on 429, batch is dead-lettered
///   - Configurable MaxRetryAttempts and MaxBackoffCeiling
///   - Dead-letter store receives the batch on exhaustion
/// </summary>
public class RetryAfterTests
{
    // -------------------------------------------------------------------------
    // Retry-After header parsing (via ApiUploadResult.ParseRetryAfterHeader)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("0", 0)]
    [InlineData("  30  ", 30)]  // leading/trailing whitespace
    public void ParseRetryAfterHeader_IntegerSeconds_Parsed(string headerValue, int expectedSeconds)
    {
        var response = MakeRateLimitedResponse(headerValue);
        var result = ApiUploadResult.ParseRetryAfterHeader(response);
        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void ParseRetryAfterHeader_HttpDate_ParsedAsDelay()
    {
        // Use a date 30 seconds in the future
        var future = DateTimeOffset.UtcNow.AddSeconds(30);
        var headerValue = future.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            System.Globalization.CultureInfo.InvariantCulture);

        var response = MakeRateLimitedResponse(headerValue);
        var result = ApiUploadResult.ParseRetryAfterHeader(response);

        Assert.NotNull(result);
        // Allow ±2 s tolerance for test execution time
        Assert.InRange(result!.Value.TotalSeconds, 27, 32);
    }

    [Fact]
    public void ParseRetryAfterHeader_HttpDateInPast_ReturnsZero()
    {
        var past = DateTimeOffset.UtcNow.AddSeconds(-10);
        var headerValue = past.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'",
            System.Globalization.CultureInfo.InvariantCulture);

        var response = MakeRateLimitedResponse(headerValue);
        var result = ApiUploadResult.ParseRetryAfterHeader(response);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.Zero, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRetryAfterHeader_AbsentOrMalformed_ReturnsNull(string? headerValue)
    {
        HttpResponseMessage response;
        if (headerValue is null)
        {
            // No Retry-After header at all
            response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }
        else
        {
            response = MakeRateLimitedResponse(headerValue);
        }

        var result = ApiUploadResult.ParseRetryAfterHeader(response);
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // 429 is not a permanent failure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_429ThenSuccess_RetriesAndSucceeds()
    {
        var ctx = new StubSessionContext("session-rl");

        var stub = new StubApiClient(
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)),
            ApiUploadResult.Success(200));

        var uploader = MakeUploader(stub, ctx);
        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(2, stub.CallCount);  // 1 rate-limit + 1 success
    }

    [Fact]
    public async Task FlushAsync_MultipleSuccessive429s_EventuallySucceeds()
    {
        var ctx = new StubSessionContext("session-rl-multi");

        var stub = new StubApiClient(
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)),
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)),
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)),
            ApiUploadResult.Success(200));

        var uploader = MakeUploader(stub, ctx, maxRetryAttempts: 5);
        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(4, stub.CallCount);
    }

    [Fact]
    public async Task FlushAsync_429WithoutRetryAfterHeader_UsesExponentialBackoff()
    {
        // Verify the uploader doesn't blow up and still retries when Retry-After is absent
        var ctx = new StubSessionContext("session-rl-noheader");

        var stub = new StubApiClient(
            ApiUploadResult.RateLimit(retryAfter: null),  // no Retry-After
            ApiUploadResult.Success(200));

        var uploader = MakeUploader(stub, ctx);
        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(2, stub.CallCount);
    }

    // -------------------------------------------------------------------------
    // 429 retry exhaustion → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_429ExhaustsRetries_DeadLettersTheBatch()
    {
        var ctx = new StubSessionContext("session-rl-exhausted");

        // Always 429 — never succeeds
        var rl429 = Enumerable.Repeat(
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)), 10).ToArray();
        var stub = new StubApiClient(rl429);

        var deadLetter = new DeadLetterStore(":memory:");

        var uploader = MakeUploader(stub, ctx, maxRetryAttempts: 3, deadLetterStore: deadLetter);
        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        // Exactly maxRetryAttempts calls were made
        Assert.Equal(3, stub.CallCount);
        // Batch was dead-lettered
        Assert.Equal(1L, deadLetter.Count());
    }

    [Fact]
    public async Task FlushAsync_PermanentFailure_DeadLettersImmediately()
    {
        var ctx = new StubSessionContext("session-perm");

        var stub = new StubApiClient(ApiUploadResult.Failure(400, isPermanent: true));

        var deadLetter = new DeadLetterStore(":memory:");
        var uploader = MakeUploader(stub, ctx, deadLetterStore: deadLetter);

        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(1, stub.CallCount);  // no retry on 400
        Assert.Equal(1L, deadLetter.Count());
    }

    // -------------------------------------------------------------------------
    // Configurable max retry attempts
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_ConfiguredMaxRetries_HonouredFor429()
    {
        var ctx = new StubSessionContext("session-config");

        var rl429 = Enumerable.Repeat(
            ApiUploadResult.RateLimit(TimeSpan.FromMilliseconds(1)), 20).ToArray();
        var stub = new StubApiClient(rl429);

        var uploader = MakeUploader(stub, ctx, maxRetryAttempts: 2);
        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public void Uploader_ConfiguredMaxBackoffCeiling_StoredCorrectly()
    {
        var ctx = new StubSessionContext("x");
        var stub = new StubApiClient();
        var uploader = new GuideSampleBatchUploader(
            stub, ctx,
            maxRetryAttempts: 7,
            maxBackoffCeiling: TimeSpan.FromSeconds(120));

        Assert.Equal(7, uploader.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(120), uploader.MaxBackoffCeiling);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MappedGuideStep MakeSample() =>
        MappedGuideStep.FromArcseconds(DateTimeOffset.UtcNow, 0.1, 0.2);

    private static GuideSampleBatchUploader MakeUploader(
        StubApiClient stub,
        StubSessionContext ctx,
        int? maxRetryAttempts = null,
        TimeSpan? maxBackoffCeiling = null,
        DeadLetterStore? deadLetterStore = null)
    {
        return new GuideSampleBatchUploader(
            stub,
            ctx,
            flushInterval: TimeSpan.FromHours(99),
            initialRetryDelay: TimeSpan.FromMilliseconds(1),
            maxRetryAttempts: maxRetryAttempts,
            maxBackoffCeiling: maxBackoffCeiling ?? TimeSpan.FromMilliseconds(5),
            deadLetterStore: deadLetterStore);
    }

    private static HttpResponseMessage MakeRateLimitedResponse(string retryAfterValue)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfterValue);
        return response;
    }

    // -------------------------------------------------------------------------
    // Stubs
    // -------------------------------------------------------------------------

    private sealed class StubSessionContext : ISessionContext
    {
        public StubSessionContext(string? sessionId) => ActiveSessionId = sessionId;
        public string? ActiveSessionId { get; }
        public bool HasActiveSession => ActiveSessionId is not null;
    }

    private sealed class StubApiClient : IGuideSamplesApi
    {
        private readonly Queue<ApiUploadResult> _results;
        public int CallCount { get; private set; }

        public StubApiClient(params ApiUploadResult[] results)
        {
            _results = new Queue<ApiUploadResult>(
                results.Length > 0 ? results : new[] { ApiUploadResult.Success(200) });
        }

        public Task<ApiUploadResult> PostGuideSamplesAsync(
            GuideSampleBatchRequest request, CancellationToken ct = default)
        {
            CallCount++;
            var result = _results.Count > 0
                ? _results.Dequeue()
                : ApiUploadResult.Failure(503, isPermanent: false);
            return Task.FromResult(result);
        }
    }
}
