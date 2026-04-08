# Subframes NINA Plugin

[![Build](https://github.com/subframes-astro/nina-plugin/actions/workflows/build.yml/badge.svg)](https://github.com/subframes-astro/nina-plugin/actions/workflows/build.yml)

A C# .NET 8 [NINA](https://nighttime-imaging.eu) plugin that captures per-exposure telemetry and ships it to the [Subframes](https://subframes.app) platform in real time.

## Download

Prebuilt plugin binaries are available on the [Releases](https://github.com/subframes-astro/nina-plugin/releases) page. Download the latest zip, extract, and follow the installation instructions below.

## What it does

- **Auto-session detection** (default, no sequence items required): detects when your sequence starts and opens a session automatically. When the sequence finishes or the rig goes idle for too long, the session closes automatically. Target changes between DSO containers are detected and tracked without manual intervention.
- **Manual session mode**: use the `Start Subframes Session` sequence item for full control over session start and target metadata.
- **Per-exposure telemetry**: after every saved image, captures filter, exposure time, gain, offset, binning, HFR, star count, guiding RMS (RA/Dec/total), ADU stats, camera temperature, and weather data, then writes it to a local SQLite cache for background upload.
- **JPEG thumbnails**: generates a 320px-wide JPEG after each exposure and uploads it alongside the frame data.
- **Live heartbeats**: sends a session heartbeat every 60 seconds with current target, filter, HFR, guiding status, uptime, and safety monitor state so the Subframes dashboard stays current between exposures.
- **Station heartbeat**: every 5 minutes sends equipment profile (telescope, camera, mount, focuser, filter wheel, accessories), site location, device connection status, safety monitor state, and plugin version — even when no session is active.
- **Safety monitor integration**: reads `IsSafe` from NINA's connected safety monitor and includes it in every heartbeat and station report.
- **Offline-first**: all frame data is written to a local SQLite queue first. A background sync engine uploads in 30-second batches and retries automatically when connectivity returns. Your imaging session is never interrupted by network issues.
- **Settings** (API URL, API key, instance name, etc.) are saved to `%APPDATA%\Subframes\nina-plugin\settings.json`.

## Requirements

- NINA 3.x (tested on 3.1+)
- .NET 8 runtime (bundled with NINA 3.x)
- A Subframes account at [subframes.io](https://subframes.io)
- A valid API key (generate one from your Subframes account settings)

## Building from source

### 1. Get the NINA SDK

Download NINA from [nighttime-imaging.eu](https://nighttime-imaging.eu) and install it, or extract a NINA release zip. You need the following DLLs:

```
NINA.Core.dll
NINA.Plugin.dll
NINA.Equipment.dll
NINA.Sequencer.dll
NINA.Astrometry.dll
NINA.WPF.Base.dll
```

Copy them into `SubframesPlugin/lib/nina-sdk/`.

Alternatively, set the `NinaSdkPath` MSBuild property at build time:

```bash
dotnet build /p:NinaSdkPath="C:\Program Files\N.I.N.A"
```

Or set the environment variable before building:

```bash
set NINA_SDK_PATH=C:\Program Files\N.I.N.A
dotnet build
```

### 2. Build

```bash
dotnet build SubframesPlugin/SubframesPlugin.csproj -c Release
```

Output is at `SubframesPlugin/bin/Release/net8.0-windows/Subframes.NinaPlugin.dll`.

## Installing in NINA

1. Locate your NINA plugins folder (default: `%LOCALAPPDATA%\NINA\Plugins\`).
2. Create a subfolder: `%LOCALAPPDATA%\NINA\Plugins\Subframes\`.
3. Copy **all DLLs** from the build output directory into that folder.
   From `SubframesPlugin/bin/Release/net8.0-windows/`, copy:
   - `Subframes.NinaPlugin.dll`
   - `Microsoft.Data.Sqlite.dll`
   - `SQLitePCLRaw.core.dll`
   - `SQLitePCLRaw.nativelibrary.dll`
   - `SQLitePCLRaw.provider.winsqlite3.dll`

   > **Important:** The plugin will fail to load in NINA if these companion DLLs are missing.
   > NINA's plugin loader resolves dependencies from the plugin's own subfolder.
4. Start NINA. The plugin will appear in the plugin manager.

## Configuring the plugin

1. In NINA, open the **Subframes** dockable panel (View > Panels > Subframes, or look in the side-panel list).
2. Enter your **API Base URL** (default: `https://api.subframes.io`).
3. Enter your **API Key** (starts with `astk_live_`).
4. Make sure **Send data to Subframes API** is checked.
5. Optionally set an **Instance Name** (e.g. `Main Scope`, `Widefield Rig`) if you run multiple rigs. The plugin auto-generates a stable **Instance ID** on first run.
6. Click **Save Settings**.

### Advanced settings

| Setting | Default | Description |
|---|---|---|
| Auto Session Detection | enabled | Automatically open/close sessions based on sequence lifecycle and target changes |
| Session Timeout (minutes) | 30 | Idle time after last exposure before an auto-session is ended |
| Cache Sync Interval (seconds) | 30 | How often the background sync engine flushes the local frame queue |
| Cache Retention (hours) | 72 | How long synced frames are kept in the local SQLite cache before cleanup |
| Debug Logging | disabled | Log JSON payloads and session lifecycle events at the NINA debug level |

## Using sequence items

All sequence items are in the **Subframes** category in the Advanced Sequencer.

> **Tip:** With auto-session detection enabled (default), you don't need to add any sequence items — sessions open and close automatically with your sequence, and target transitions are detected without manual items.

| Item | Purpose |
|---|---|
| `Start Subframes Session` | Manually open a new session. Use this when you want explicit control over the session start time and initial target. |
| `End Subframes Session` | Manually close the active session. |
| `Start Subframes Target` | Manually signal that a new target is starting within the current session. |
| `End Subframes Target` | Manually signal that the current target has finished. |
| `Set Session Status` | Explicitly set the session status (`active`, `waiting`, `paused`) with an optional reason message. |

## Sequence flow (auto-session mode)

```
[Sequence Start]
  +-- Plugin detects sequence started
  +-- Opens session via POST /api/v1/ingest/session/start
  +-- Sends immediate station heartbeat with equipment profile

[Each Target (DSO Container)]
  +-- Plugin detects container RUNNING via PropertyChanged
  +-- Registers target via POST /api/v1/ingest/session/target/start
  +-- Take Exposure
       +-- ImageSaved fires
       +-- Frame cached to SQLite (filter, HFR, guiding RMS, ADU stats, ...)
       +-- 320px JPEG thumbnail generated and uploaded
       +-- Background SyncEngine batches frames → POST /api/v1/ingest/frame
  +-- Session heartbeat fires every 60s → POST /api/v1/ingest/heartbeat
  +-- Target ends → POST /api/v1/ingest/session/target/end

[Sequence End]
  +-- Remaining frames flushed to API
  +-- Session closed via POST /api/v1/ingest/session/end
  +-- Station heartbeat status → "online"
```

## API contract

The plugin targets the Subframes ingest API. All endpoints require API key auth via `Authorization: Bearer <api_key>`.

### POST /api/v1/ingest/session/start

```json
Request: {
  "targetName": "M42",
  "targetRa": 83.8221,
  "targetDec": -5.3911,
  "startTime": "2024-01-15T22:00:00Z",
  "targetType": "Galaxy",            // optional
  "equipmentProfileId": "uuid",      // optional
  "locationLat": 48.1,               // optional
  "locationLon": 11.6,               // optional
  "locationLabel": "Backyard",       // optional
  "bortleZone": 4,                   // optional
  "instanceId": "uuid",              // optional, auto-generated per rig
  "instanceName": "Main Scope"       // optional
}
Response: {
  "data": { "sessionId": "uuid", "status": "active" },
  "error": null
}
```

### POST /api/v1/ingest/session/end

```json
Request: {
  "sessionId": "uuid",
  "endTime": "2024-01-16T04:00:00Z"
}
Response: {
  "data": { "status": "completed" },
  "error": null
}
```

### POST /api/v1/ingest/session/target/start

```json
Request: {
  "sessionId": "uuid",
  "targetName": "M42",
  "targetRa": 83.8221,
  "targetDec": -5.3911,
  "startTime": "2024-01-15T22:05:00Z",
  "targetType": "Nebula"             // optional
}
Response: {
  "data": { "sessionTargetId": "uuid", "status": "active" },
  "error": null
}
```

### POST /api/v1/ingest/session/target/end

```json
Request: {
  "sessionId": "uuid",
  "sessionTargetId": "uuid",
  "endTime": "2024-01-15T23:30:00Z"
}
Response: {
  "data": { "status": "completed" },
  "error": null
}
```

### POST /api/v1/ingest/session/status

```json
Request: {
  "sessionId": "uuid",
  "status": "waiting",               // "active" | "waiting" | "paused"
  "waitReason": "Autofocus running"  // optional
}
Response: {
  "data": { "status": "waiting" },
  "error": null
}
```

### POST /api/v1/ingest/frame

```json
Request: {
  "sessionId": "uuid",
  "frames": [{
    "frameNumber": 1,
    "sessionTargetId": "uuid",       // optional, links frame to active target
    "filter": "Ha",
    "exposureTime": 300.0,
    "gain": 100,
    "offset": 10,
    "binning": 1,
    "hfr": 2.5,
    "hfrStdev": 0.3,
    "starCount": 150,
    "rmsRa": 0.41,                   // arcsec
    "rmsDec": 0.38,                  // arcsec
    "rmsTotal": 0.56,                // arcsec
    "meanAdu": 1024.5,
    "medianAdu": 1020.0,
    "stdevAdu": 45.2,
    "minAdu": 850,
    "maxAdu": 65535,
    "cameraTemp": -10.0,
    "ambientTemp": 12.5,             // from weather device
    "humidity": 55.0,                // %
    "dewPoint": 3.2,                 // °C
    "windSpeed": 4.1,                // m/s
    "cloudCover": 0.0,               // %
    "skyQuality": 21.5,              // mag/arcsec²
    "capturedAt": "2024-01-15T22:05:00Z"
  }]
}
Response: {
  "data": { "accepted": 1, "rejected": 0, "sessionId": "uuid", "totalFrames": 1 },
  "error": null
}
```

### POST /api/v1/ingest/heartbeat

Sent every 60 seconds while a session is active.

```json
Request: {
  "sessionId": "uuid",
  "status": "imaging",
  "currentTarget": "M42",
  "currentFilter": "Ha",
  "exposureCount": 42,
  "latestHfr": 2.5,
  "latestRmsTotal": 0.56,
  "guidingStatus": "guiding",
  "uptimeMinutes": 120,
  "isSafe": true,                    // from safety monitor, null if not connected
  "instanceId": "uuid",
  "instanceName": "Main Scope"
}
```

### POST /api/v1/ingest/station/heartbeat

Sent every 5 minutes regardless of whether a session is active.

```json
Request: {
  "instanceId": "uuid",
  "instanceName": "Main Scope",
  "status": "imaging",               // "online" | "imaging"
  "pluginVersion": "0.1.36",
  "isSafe": true,                    // from safety monitor, null if not connected
  "equipment": {
    "telescopeName": "Celestron EdgeHD 8",
    "focalLength": 2032.0,
    "aperture": 203.2,
    "cameraName": "ZWO ASI2600MM Pro",
    "pixelSize": 3.76,
    "mountName": "iOptron CEM60",
    "filterWheel": "ZWO EFW 7x36",
    "filters": ["Ha", "OIII", "SII", "L", "R", "G", "B"],
    "devices": [
      { "category": "Camera", "name": "ZWO ASI2600MM Pro", "connected": true },
      { "category": "Mount", "name": "iOptron CEM60", "connected": true },
      { "category": "Focuser", "name": "Pegasus FocusCube", "connected": true },
      { "category": "SafetyMonitor", "name": "Boltwood Cloud Sensor", "connected": true }
    ]
  },
  "location": {
    "latitude": 48.137,
    "longitude": 11.576,
    "label": "Backyard",
    "elevationMeters": 520.0
  }
}
```

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "could not start session" in NINA log | API not running, wrong URL, or missing API key | Check the base URL and verify your API key in plugin settings |
| Exposures not appearing in dashboard | Auto-session detection off and no sequence item | Enable auto-session detection in plugin settings, or add `Start Subframes Session` to your sequence |
| Session opens but no target shown | Target coordinates are 0,0 or target name is empty | The plugin skips RA=0/Dec=0 as invalid; ensure your DSO container has a real target set |
| 401 Unauthorized in NINA log | Invalid or revoked API key | Generate a new API key and update plugin settings |
| Plugin not visible in NINA | DLL not in plugins folder | Re-check the install path; check NINA's plugin log for load errors |
| "Could not load file or assembly 'Microsoft.Data.Sqlite'" in NINA log | Dependency DLLs not copied | Copy **all DLLs** from the build output into `%LOCALAPPDATA%\NINA\Plugins\Subframes\` — not just `Subframes.NinaPlugin.dll` |
| No thumbnails uploading | Network issue or API unreachable | Thumbnails are fire-and-forget; check NINA log for `SendThumbnail error` warnings |
| Safety monitor `isSafe` always null | Safety monitor not connected in NINA | Connect a safety monitor device in NINA's equipment tab |
| Frames queued but not syncing | API unreachable during session | The SyncEngine retries automatically; frames upload once connectivity returns |

## License

GPL-3.0. See [LICENSE](LICENSE) for details.
