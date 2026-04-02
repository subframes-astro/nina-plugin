using System.Text.Json.Serialization;

namespace Subframes.NinaPlugin.Api;

// ── Requests ─────────────────────────────────────────────────────────────────

/// <summary>Body for POST /api/v1/ingest/session/start</summary>
public sealed class StartSessionRequest
{
    [JsonPropertyName("targetName")]
    public required string TargetName { get; init; }

    [JsonPropertyName("targetRa")]
    public double TargetRa { get; init; }

    [JsonPropertyName("targetDec")]
    public double TargetDec { get; init; }

    [JsonPropertyName("startTime")]
    public required string StartTime { get; init; }

    [JsonPropertyName("targetType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetType { get; init; }

    [JsonPropertyName("equipmentProfileId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EquipmentProfileId { get; init; }

    [JsonPropertyName("locationLat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LocationLat { get; init; }

    [JsonPropertyName("locationLon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LocationLon { get; init; }

    [JsonPropertyName("locationLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationLabel { get; init; }

    [JsonPropertyName("bortleZone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public short? BortleZone { get; init; }
}

/// <summary>Body for POST /api/v1/ingest/session/end</summary>
public sealed class EndSessionRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("endTime")]
    public required string EndTime { get; init; }
}

/// <summary>Body for POST /api/v1/ingest/heartbeat</summary>
public sealed class HeartbeatRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("currentTarget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentTarget { get; init; }

    [JsonPropertyName("currentFilter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentFilter { get; init; }

    [JsonPropertyName("exposureCount")]
    public int ExposureCount { get; init; }

    [JsonPropertyName("latestHfr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LatestHfr { get; init; }

    [JsonPropertyName("latestRmsTotal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? LatestRmsTotal { get; init; }

    [JsonPropertyName("guidingStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GuidingStatus { get; init; }

    [JsonPropertyName("uptimeMinutes")]
    public int UptimeMinutes { get; init; }
}

/// <summary>Body for POST /api/v1/ingest/frame</summary>
public sealed class IngestFramesRequest
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("frames")]
    public required List<FrameInput> Frames { get; init; }
}

/// <summary>A single frame within an IngestFramesRequest batch.</summary>
public sealed class FrameInput
{
    [JsonPropertyName("frameNumber")]
    public int FrameNumber { get; init; }

    [JsonPropertyName("exposureTime")]
    public double ExposureTime { get; init; }

    [JsonPropertyName("capturedAt")]
    public required string CapturedAt { get; init; }

    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filter { get; init; }

    [JsonPropertyName("gain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Gain { get; init; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    [JsonPropertyName("binning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public short? Binning { get; init; }

    [JsonPropertyName("hfr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Hfr { get; init; }

    [JsonPropertyName("hfrStdev")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? HfrStdev { get; init; }

    [JsonPropertyName("starCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StarCount { get; init; }

    [JsonPropertyName("rmsRa")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RmsRa { get; init; }

    [JsonPropertyName("rmsDec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RmsDec { get; init; }

    [JsonPropertyName("rmsTotal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RmsTotal { get; init; }

    [JsonPropertyName("meanAdu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MeanAdu { get; init; }

    [JsonPropertyName("medianAdu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MedianAdu { get; init; }

    [JsonPropertyName("stdevAdu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? StdevAdu { get; init; }

    [JsonPropertyName("minAdu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinAdu { get; init; }

    [JsonPropertyName("maxAdu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxAdu { get; init; }

    [JsonPropertyName("cameraTemp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CameraTemp { get; init; }

    [JsonPropertyName("ambientTemp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AmbientTemp { get; init; }

    [JsonPropertyName("humidity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Humidity { get; init; }

    [JsonPropertyName("dewPoint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DewPoint { get; init; }

    [JsonPropertyName("windSpeed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WindSpeed { get; init; }

    [JsonPropertyName("cloudCover")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CloudCover { get; init; }

    [JsonPropertyName("skyQuality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SkyQuality { get; init; }
}

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>Standard API envelope: { "data": T, "error": { "code", "message" } }</summary>
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("error")]
    public ApiError? Error { get; init; }
}

public sealed class ApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Response payload from POST /api/v1/ingest/session/start</summary>
public sealed class StartSessionData
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>Response payload from POST /api/v1/ingest/session/end</summary>
public sealed class EndSessionData
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>Response payload from POST /api/v1/ingest/frame</summary>
public sealed class IngestFramesData
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; init; }

    [JsonPropertyName("rejected")]
    public int Rejected { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("totalFrames")]
    public int TotalFrames { get; init; }
}
