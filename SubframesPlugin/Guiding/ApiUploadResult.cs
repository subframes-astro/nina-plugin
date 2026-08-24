using System.Globalization;
using System.Net;
using System.Net.Http;

namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Lightweight result type for guide-sample API calls that avoids exception-driven control flow.
/// Also exposes the static <see cref="ParseRetryAfterHeader"/> helper used by the batch uploader
/// and tests.
/// </summary>
public sealed class ApiUploadResult
{
    public bool IsSuccess { get; private init; }

    /// <summary>True for 4xx responses where retrying is pointless (e.g. 400, 401, 403).</summary>
    public bool IsPermanentFailure { get; private init; }

    /// <summary>True when the server returned 429 Too Many Requests.</summary>
    public bool IsRateLimited { get; private init; }

    /// <summary>HTTP status code, or null for network-level failures.</summary>
    public int? StatusCode { get; private init; }

    /// <summary>
    /// Delay indicated by the <c>Retry-After</c> response header (429 responses only).
    /// Null if the header was absent or unparseable — caller should use exponential back-off.
    /// </summary>
    public TimeSpan? RetryAfter { get; private init; }

    public static ApiUploadResult Success(int statusCode) =>
        new() { IsSuccess = true, StatusCode = statusCode };

    public static ApiUploadResult Failure(int? statusCode, bool isPermanent) =>
        new() { IsSuccess = false, StatusCode = statusCode, IsPermanentFailure = isPermanent };

    public static ApiUploadResult RateLimit(TimeSpan? retryAfter) =>
        new() { IsSuccess = false, IsRateLimited = true, StatusCode = 429, RetryAfter = retryAfter };

    // -------------------------------------------------------------------------
    // Retry-After header parsing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses the <c>Retry-After</c> response header.
    /// Supports both the integer-seconds form and the HTTP-date form.
    /// Returns <c>null</c> if the header is absent or cannot be parsed.
    /// </summary>
    public static TimeSpan? ParseRetryAfterHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Try integer seconds first (most common)
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // Try HTTP-date format (e.g. "Thu, 17 Jul 2026 12:34:56 GMT")
        if (DateTimeOffset.TryParseExact(
                raw.Trim(),
                "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var retryAt))
        {
            var delay = retryAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Returns true for 4xx errors that are genuinely permanent (bad request, auth, etc.).
    /// 429 (rate limited) is explicitly excluded — it is retriable.
    /// </summary>
    internal static bool IsClientError(HttpStatusCode code) =>
        (int)code is >= 400 and < 500 && code != HttpStatusCode.TooManyRequests;
}
