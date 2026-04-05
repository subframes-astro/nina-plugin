# Subframes NINA Plugin

[![Build](https://github.com/subframes-astro/nina-plugin/actions/workflows/build.yml/badge.svg)](https://github.com/subframes-astro/nina-plugin/actions/workflows/build.yml)

A C# .NET 8 [NINA](https://nighttime-imaging.eu) plugin that captures per-exposure telemetry and POSTs it to the [Subframes](https://subframes.app) ingest API after every saved image.

## Download

Prebuilt plugin binaries are available on the [Releases](https://github.com/subframes-astro/nina-plugin/releases) page. Download the latest zip, extract, and follow the installation instructions below.

## What it does

- On sequence start: creates a new imaging session via `POST /api/v1/ingest/session/start`
- After every saved image: fires `POST /api/v1/ingest/frame` with frame metadata (filter, exposure time, gain, offset, HFR, star count, guiding RMS, camera temp, and more)
- On sequence end: closes the session via `POST /api/v1/ingest/session/end`
- If the API is unreachable, the plugin logs a warning and **continues the sequence normally** — your imaging is never interrupted
- Settings (API URL, API key, enable/disable) are saved to `%APPDATA%\Subframes\nina-plugin\settings.json`

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
2. Enter your **API Base URL** (e.g. `http://localhost:8080`).
3. Enter your **API Key** (starts with `astk_live_`).
4. Make sure **Send data to Subframes API** is checked.
5. Click **Save Settings**.

## Adding to a sequence

1. In the Advanced Sequencer, find the **Subframes** category in the instruction list.
2. Drag **"Start Subframes Session"** into your sequence — ideally inside the **Sequence Start** container or **Before Each Target**.
3. Set the **Target Name**, **RA**, and **Dec** properties to match your imaging target.
4. Run your sequence. Every saved image triggers a POST to the API.

## Sequence flow

```
[Sequence Start]
  +-- Start Subframes Session  <-- POSTs to /api/v1/ingest/session/start, stores session ID
  +-- ... (slew, plate solve, autofocus, etc.)

[Each Target]
  +-- Take Exposure
       +-- ImageSaved event fires --> plugin POSTs to /api/v1/ingest/frame

[Sequence End]
  +-- Session ended --> plugin POSTs to /api/v1/ingest/session/end
```

## API contract

The plugin targets the Subframes Go backend ingest API. All endpoints require API key auth via `Authorization: Bearer <api_key>`.

### POST /api/v1/ingest/session/start

```json
Request: {
  "targetName": "M42",
  "targetRa": 83.8221,
  "targetDec": -5.3911,
  "startTime": "2024-01-15T22:00:00Z"
}
Response: {
  "data": { "sessionId": "uuid", "status": "active" },
  "error": null
}
```

### POST /api/v1/ingest/frame

```json
Request: {
  "sessionId": "uuid",
  "frames": [{
    "frameNumber": 1,
    "filter": "Ha",
    "exposureTime": 300.0,
    "gain": 100,
    "offset": 10,
    "binning": 1,
    "hfr": 2.5,
    "starCount": 150,
    "cameraTemp": -10.0,
    "capturedAt": "2024-01-15T22:05:00Z"
  }]
}
Response: {
  "data": { "accepted": 1, "rejected": 0, "sessionId": "uuid", "totalFrames": 1 },
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

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "could not start session" in NINA log | API not running, wrong URL, or missing API key | Start the Subframes API (`docker compose up`), check the base URL, and verify your API key in plugin settings |
| Exposures not appearing in dashboard | Session item not in sequence | Add "Start Subframes Session" to your sequence start |
| 401 Unauthorized in NINA log | Invalid or revoked API key | Generate a new API key and update plugin settings |
| Plugin not visible in NINA | DLL not in plugins folder | Re-check the install path; check NINA's plugin log for load errors |
| "Could not load file or assembly 'Microsoft.Data.Sqlite'" in NINA log | Dependency DLLs not copied | Copy **all DLLs** from the build output into `%LOCALAPPDATA%\NINA\Plugins\Subframes\` — not just `Subframes.NinaPlugin.dll` |

## License

GPL-3.0. See [LICENSE](LICENSE) for details.
