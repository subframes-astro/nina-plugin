using Subframes.NinaPlugin.Api;
using Subframes.NinaPlugin.Guiding;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Tests for <see cref="GuideSampleBatchUploader"/> batching, flush, and retry logic.
/// Uses a stub <see cref="IGuideSamplesApi"/> implementation — no HTTP, no NINA SDK.
/// </summary>
public class GuideSampleBatchUploaderTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MappedGuideStep MakeSample(double ra = 0.1, double dec = 0.2) =>
        MappedGuideStep.FromArcseconds(DateTimeOffset.UtcNow, ra, dec);

    private static (GuideSampleBatchUploader uploader, StubApiClient stub, StubSessionContext ctx)
        MakeUploader(TimeSpan? flushInterval = null)
    {
        var ctx = new StubSessionContext("test-session-id");
        var stub = new StubApiClient();
        var uploader = new GuideSampleBatchUploader(
            stub,
            ctx,
            flushInterval ?? TimeSpan.FromHours(99), // no auto-flush in unit tests
            initialRetryDelay: TimeSpan.FromMilliseconds(1)); // fast retry in tests

        return (uploader, stub, ctx);
    }

    // -------------------------------------------------------------------------
    // Basic enqueue / flush
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_EmptyQueue_DoesNotCallApi()
    {
        var (uploader, stub, _) = MakeUploader();

        await uploader.FlushAsync();

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task FlushAsync_UploadsEnqueuedSamples()
    {
        var (uploader, stub, _) = MakeUploader();
        uploader.Enqueue(MakeSample(0.1, 0.2));
        uploader.Enqueue(MakeSample(0.3, 0.4));

        await uploader.FlushAsync();

        Assert.Equal(1, stub.CallCount);
        Assert.Equal(2, stub.LastBatch!.Samples.Count);
    }

    [Fact]
    public async Task FlushAsync_NoActiveSession_DiscardsQueue()
    {
        var ctx = new StubSessionContext(null); // no active session
        var stub = new StubApiClient();
        var uploader = new GuideSampleBatchUploader(stub, ctx);

        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(0, stub.CallCount); // nothing uploaded
    }

    // -------------------------------------------------------------------------
    // Batch size cap
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_MoreThanMaxBatchSize_OnlyUploadsMaxBatch()
    {
        var (uploader, stub, _) = MakeUploader();

        for (int i = 0; i < GuideSampleBatchUploader.MaxBatchSize + 100; i++)
            uploader.Enqueue(MakeSample());

        await uploader.FlushAsync();

        // First flush uploads exactly MaxBatchSize
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(GuideSampleBatchUploader.MaxBatchSize, stub.LastBatch!.Samples.Count);
    }

    // -------------------------------------------------------------------------
    // Retry on transient failure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_TransientFailureThenSuccess_UploadSucceeds()
    {
        var ctx = new StubSessionContext("session-abc");

        // Fail twice, then succeed
        var stub = new StubApiClient(
            ApiUploadResult.Failure(503, isPermanent: false),
            ApiUploadResult.Failure(503, isPermanent: false),
            ApiUploadResult.Success(200));

        var uploader = new GuideSampleBatchUploader(
            stub, ctx,
            flushInterval: TimeSpan.FromHours(99),
            initialRetryDelay: TimeSpan.FromMilliseconds(1));

        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(3, stub.CallCount); // 2 failures + 1 success
    }

    [Fact]
    public async Task FlushAsync_PermanentFailure_DoesNotRetry()
    {
        var ctx = new StubSessionContext("session-xyz");

        var stub = new StubApiClient(ApiUploadResult.Failure(400, isPermanent: true));

        var uploader = new GuideSampleBatchUploader(
            stub, ctx,
            flushInterval: TimeSpan.FromHours(99),
            initialRetryDelay: TimeSpan.FromMilliseconds(1));

        uploader.Enqueue(MakeSample());
        await uploader.FlushAsync();

        Assert.Equal(1, stub.CallCount); // exactly 1 attempt, not retried
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
        public GuideSampleBatchRequest? LastBatch { get; private set; }

        public StubApiClient(params ApiUploadResult[] results)
        {
            _results = new Queue<ApiUploadResult>(
                results.Length > 0 ? results : new[] { ApiUploadResult.Success(200) });
        }

        public Task<ApiUploadResult> PostGuideSamplesAsync(
            GuideSampleBatchRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            LastBatch = request;

            var result = _results.Count > 0
                ? _results.Dequeue()
                : ApiUploadResult.Failure(503, isPermanent: false);

            return Task.FromResult(result);
        }
    }
}
