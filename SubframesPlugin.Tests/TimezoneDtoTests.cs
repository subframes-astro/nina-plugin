using System.Text.Json;
using Subframes.NinaPlugin.Api;
using Xunit;

namespace Subframes.NinaPlugin.Tests;

/// <summary>
/// Verifies that the timezone field is present on the DTOs that send it
/// (StationLocationDto and StartSessionRequest) and that JSON serialization
/// emits the correct key name.
/// </summary>
public sealed class TimezoneDtoTests
{
    // ── StationLocationDto ────────────────────────────────────────────────────

    [Fact]
    public void StationLocationDto_Timezone_SerializesAsTimezoneKey()
    {
        var dto = new StationLocationDto
        {
            Latitude        = 51.5,
            Longitude       = -0.1,
            Label           = "Home Observatory",
            ElevationMeters = 30.0,
            Timezone        = "Europe/London",
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"timezone\"", json);
        Assert.Contains("\"Europe/London\"", json);
    }

    [Fact]
    public void StationLocationDto_Timezone_OmittedWhenNull()
    {
        var dto = new StationLocationDto
        {
            Latitude  = 51.5,
            Longitude = -0.1,
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("\"timezone\"", json);
    }

    // ── StartSessionRequest ───────────────────────────────────────────────────

    [Fact]
    public void StartSessionRequest_Timezone_SerializesAsTimezoneKey()
    {
        var request = new StartSessionRequest
        {
            TargetName = "M31",
            StartTime  = "2026-04-20T22:00:00Z",
            Timezone   = "America/New_York",
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"timezone\"", json);
        Assert.Contains("\"America/New_York\"", json);
    }

    [Fact]
    public void StartSessionRequest_Timezone_OmittedWhenNull()
    {
        var request = new StartSessionRequest
        {
            TargetName = "M31",
            StartTime  = "2026-04-20T22:00:00Z",
            Timezone   = null,
        };

        var json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain("\"timezone\"", json);
    }

    // ── EquipmentProfileName ──────────────────────────────────────────────────

    [Fact]
    public void StartSessionRequest_EquipmentProfileName_SerializesWithCorrectKey()
    {
        var request = new StartSessionRequest
        {
            TargetName           = "M42",
            StartTime            = "2026-04-20T22:00:00Z",
            EquipmentProfileName = "My Imaging Rig",
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"equipmentProfileName\"", json);
        Assert.Contains("\"My Imaging Rig\"", json);
    }

    [Fact]
    public void StartSessionRequest_EquipmentProfileName_OmittedWhenNull()
    {
        var request = new StartSessionRequest
        {
            TargetName           = "M42",
            StartTime            = "2026-04-20T22:00:00Z",
            EquipmentProfileName = null,
        };

        var json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain("\"equipmentProfileName\"", json);
    }
}
