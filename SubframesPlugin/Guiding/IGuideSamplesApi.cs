namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Abstraction over the guide-samples POST endpoint used by
/// <see cref="GuideSampleBatchUploader"/>. Implemented by
/// <see cref="Subframes.NinaPlugin.Api.SubframesClient"/> in production and
/// by test stubs in unit tests.
/// </summary>
public interface IGuideSamplesApi
{
    /// <summary>
    /// POST a batch of guide samples to the Subframes API.
    /// Returns an <see cref="ApiUploadResult"/> describing success or failure.
    /// Never throws for expected HTTP error responses.
    /// </summary>
    Task<ApiUploadResult> PostGuideSamplesAsync(
        GuideSampleBatchRequest request,
        CancellationToken cancellationToken = default);
}
