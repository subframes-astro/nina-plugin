using System.IO;
using System.Net.Http;
using NINA.Core.Utility;

namespace Subframes.NinaPlugin;

/// <summary>
/// Background service that determines Target Scheduler (TS) availability for the
/// connected NINA instance.
///
/// Three states:
///   <c>"none"</c>   — TS assembly not found; TS is not installed.
///   <c>"no_api"</c> — TS assembly found but its local HTTP API is not responding.
///   <c>"active"</c> — TS assembly found and HTTP API is reachable.
///
/// The assembly-presence check runs once at <see cref="Start"/> (MEF does not unload
/// assemblies mid-session). If TS is detected, an HTTP probe fires immediately and
/// re-runs every 60 seconds.
/// </summary>
internal sealed class TargetSchedulerDetector : IDisposable
{
    // Shared HttpClient with a 2-second timeout per the spec.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly int _port;
    private volatile string _currentState = "none";
    private CancellationTokenSource? _cts;
    private Task? _probeLoopTask;

    /// <param name="port">TS local HTTP API port (default 8188).</param>
    public TargetSchedulerDetector(int port = 8188)
    {
        _port = port;
    }

    /// <summary>The TS HTTP API port this detector is probing.</summary>
    public int Port => _port;

    /// <summary>
    /// Current TS availability state: <c>"none"</c> | <c>"no_api"</c> | <c>"active"</c>.
    /// Safe to read from any thread.
    /// </summary>
    public string CurrentState => _currentState;

    /// <summary>
    /// Performs the one-time assembly presence check and, if TS is installed, starts
    /// the background HTTP probe loop. Safe to call multiple times (re-entrant).
    /// </summary>
    public void Start()
    {
        Stop();

        if (!IsAssemblyPresent())
        {
            _currentState = "none";
            Logger.Debug("[Subframes] TargetSchedulerDetector: TS not found — state=none.");
            return;
        }

        Logger.Debug("[Subframes] TargetSchedulerDetector: TS assembly found — starting HTTP probe loop.");
        var cts = new CancellationTokenSource();
        _cts = cts;
        _probeLoopTask = RunProbeLoopAsync(cts.Token);
    }

    /// <summary>Cancels the probe loop and resets state to <c>"none"</c>.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _probeLoopTask = null;
        _currentState = "none";
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the TS plugin appears to be installed: either its assembly is
    /// already loaded by MEF, or its plugin directory exists on disk.
    /// </summary>
    private static bool IsAssemblyPresent()
    {
        // Fast path: assembly loaded in the current AppDomain (typical when NINA started with TS).
        if (AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name?.Contains("TargetScheduler", StringComparison.OrdinalIgnoreCase) == true))
            return true;

        // Filesystem fallback: check %localappdata%\NINA\Plugins\ for a TS subfolder.
        try
        {
            var pluginsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins");
            if (Directory.Exists(pluginsDir))
            {
                return Directory.EnumerateDirectories(pluginsDir)
                    .Any(d => Path.GetFileName(d)
                        .Contains("TargetScheduler", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[Subframes] TargetSchedulerDetector: filesystem check error: {ex.Message}");
        }

        return false;
    }

    private async Task RunProbeLoopAsync(CancellationToken ct)
    {
        // Immediate probe so the first heartbeat already has an accurate state.
        await DoProbeAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await DoProbeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — Stop() was called.
        }
        catch (Exception ex)
        {
            Logger.Error($"[Subframes] TargetSchedulerDetector: probe loop terminated unexpectedly: {ex.Message}");
        }
    }

    private async Task DoProbeAsync(CancellationToken ct)
    {
        var newState = "no_api";
        try
        {
            var url = $"http://localhost:{_port}/ts/v0/version";
            var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                newState = "active";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate shutdown signal to the loop.
        }
        catch
        {
            // Timeout, connection refused, HTTP error — state remains "no_api".
        }

        if (_currentState != newState)
        {
            Logger.Info($"[Subframes] TargetSchedulerDetector: state {_currentState} → {newState}");
            _currentState = newState;
        }
    }
}
