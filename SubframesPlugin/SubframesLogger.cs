using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Subframes.NinaPlugin;

/// <summary>
/// Static logging wrapper for the Subframes plugin.
/// <list type="bullet">
///   <item>Always forwards all messages to NINA's global <c>Logger</c> with a
///   <c>[Subframes]</c> prefix — NINA's log level rules filter as normal.</item>
///   <item>When NINA's log level is Debug or Trace, also writes to a dedicated
///   log file at <c>%LOCALAPPDATA%\NINA\Logs\subframes-{launchTimestamp}.log</c> that
///   captures <em>all</em> Subframes messages regardless of level.</item>
/// </list>
///
/// <para>
/// The debug log file is created once per NINA launch (not per calendar day), matching
/// NINA's own log file behaviour. The logger dynamically monitors NINA's global log level
/// and enables/disables file logging accordingly — if the user changes from INFO to DEBUG
/// mid-session, file logging will start on the next 5-second poll tick; if they change
/// back, any queued entries are flushed and file logging stops.
/// </para>
///
/// <para>
/// Mid-session level changes are detected via a 5-second poll of
/// <c>NINA.Core.Utility.Logger.IsEnabled(LogLevelEnum.DEBUG)</c> — the public API that
/// NINA exposes for exactly this purpose. No reflection, no NLog dependency.
/// </para>
///
/// Call <see cref="Initialize"/> from <c>Plugin.OnImportsSatisfied()</c> and
/// <see cref="Shutdown"/> from <c>Plugin.Teardown()</c>.
/// </summary>
public static class SubframesLogger
{
    private static readonly ConcurrentQueue<string> _logQueue = new();
    private static volatile bool _fileLoggingActive;
    private static volatile bool _initialized;
    private static string? _logDirectory;
    private static string? _logFilePath;
    private static Timer? _flushTimer;
    private static Timer? _levelCheckTimer;
    private static readonly object _writeLock = new();

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the Subframes logger. Sets up a periodic check of NINA's global log
    /// level to dynamically enable/disable the dedicated debug log file.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        try
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Logs");
            Directory.CreateDirectory(_logDirectory);

            // Fixed filename for this NINA launch session (not date-based).
            string launchTimestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            _logFilePath = Path.Combine(_logDirectory, $"subframes-{launchTimestamp}.log");

            // Check log level now and adapt.
            EvaluateLogLevel();

            // Poll every 5 seconds to detect when the user changes the log level
            // in NINA's Options (NINA calls Logger.SetLogLevel() which updates the
            // LoggingLevelSwitch immediately — our next poll tick will catch it).
            _levelCheckTimer = new Timer(_ => EvaluateLogLevel(), null,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            // Flush the queue every 2 seconds to avoid blocking the imaging thread.
            _flushTimer = new Timer(_ => FlushQueue(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _fileLoggingActive = false;
            NINA.Core.Utility.Logger.Warning($"[Subframes] Failed to initialise debug logger: {ex.Message}");
        }
    }

    /// <summary>
    /// Flushes pending log entries and stops the dedicated Subframes log file writer.
    /// Safe to call even when <see cref="Initialize"/> was never called.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _initialized = false;
            _fileLoggingActive = false;
            _levelCheckTimer?.Dispose();
            _levelCheckTimer = null;
            _flushTimer?.Dispose();
            _flushTimer = null;

            // Final flush of any remaining entries.
            FlushQueue();
        }
        catch
        {
            // Never crash during plugin teardown.
        }
    }

    // ── Logging methods ─────────────────────────────────────────────────────

    /// <summary>Logs an INFO message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Info(string message)
    {
        NINA.Core.Utility.Logger.Info($"[Subframes] {message}");
        EnqueueFile("INFO ", message);
    }

    /// <summary>Logs a DEBUG message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Debug(string message)
    {
        NINA.Core.Utility.Logger.Debug($"[Subframes] {message}");
        EnqueueFile("DEBUG", message);
    }

    /// <summary>Logs a WARNING message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Warning(string message)
    {
        NINA.Core.Utility.Logger.Warning($"[Subframes] {message}");
        EnqueueFile("WARN ", message);
    }

    /// <summary>Logs an ERROR message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Error(string message)
    {
        NINA.Core.Utility.Logger.Error($"[Subframes] {message}");
        EnqueueFile("ERROR", message);
    }

    /// <summary>Logs a TRACE message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Trace(string message)
    {
        NINA.Core.Utility.Logger.Trace($"[Subframes] {message}");
        EnqueueFile("TRACE", message);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void EnqueueFile(string level, string message)
    {
        if (!_fileLoggingActive)
            return;

        string entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff} | {level} | [Subframes] {message}";
        _logQueue.Enqueue(entry);
    }

    private static void FlushQueue()
    {
        if (_logFilePath == null || _logQueue.IsEmpty)
            return;

        lock (_writeLock)
        {
            try
            {
                using var writer = new StreamWriter(_logFilePath, append: true);
                while (_logQueue.TryDequeue(out string? entry))
                {
                    writer.WriteLine(entry);
                }
            }
            catch
            {
                // Silently drop log entries if we can't write.
                // Clear the queue to avoid unbounded memory growth.
                while (_logQueue.TryDequeue(out _)) { }
            }
        }
    }

    private static volatile bool _firstCheckDone;

    /// <summary>
    /// Checks NINA's current log level and enables or disables file logging accordingly.
    /// Called on init and periodically thereafter to react to runtime changes.
    /// </summary>
    private static void EvaluateLogLevel()
    {
        try
        {
            bool debugEnabled = IsNinaDebugLevelEnabled();

            if (debugEnabled && !_fileLoggingActive)
            {
                _fileLoggingActive = true;
                WriteFileHeader();
                NINA.Core.Utility.Logger.Info($"[Subframes] Debug log file enabled: {_logFilePath}");
            }
            else if (!debugEnabled && _fileLoggingActive)
            {
                _fileLoggingActive = false;
                NINA.Core.Utility.Logger.Info("[Subframes] Debug log file disabled — NINA log level changed to INFO or higher.");
                // Flush immediately so queued entries reach disk before we stop.
                FlushQueue();
            }
            else if (!debugEnabled && !_firstCheckDone)
            {
                NINA.Core.Utility.Logger.Info("[Subframes] Debug log file inactive — NINA log level is INFO or higher.");
            }

            _firstCheckDone = true;
        }
        catch
        {
            // Don't crash the timer callback.
        }
    }

    /// <summary>
    /// Writes a session-start header to the log file so it is clear when file logging
    /// was enabled and from which NINA session. Called each time file logging transitions
    /// from inactive to active (including mid-session level changes).
    /// </summary>
    private static void WriteFileHeader()
    {
        if (_logFilePath == null)
            return;
        try
        {
            lock (_writeLock)
            {
                using var writer = new StreamWriter(_logFilePath, append: true);
                writer.WriteLine();
                writer.WriteLine($"{'=',-80}");
                writer.WriteLine($"  Subframes debug log  — file logging enabled at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"{'=',-80}");
            }
        }
        catch
        {
            // Non-fatal: session header is cosmetic.
        }
    }

    /// <summary>
    /// Returns <c>true</c> when NINA's log configuration currently enables the Debug level.
    /// <para>
    /// NINA uses Serilog internally with a <c>LoggingLevelSwitch</c>. Its
    /// <c>NINA.Core.Utility.Logger</c> class exposes a public static
    /// <c>IsEnabled(LogLevelEnum)</c> method that reads directly from that switch —
    /// the authoritative, zero-reflection API for querying the current log level.
    /// </para>
    /// </summary>
    private static bool IsNinaDebugLevelEnabled()
    {
        return NINA.Core.Utility.Logger.IsEnabled(NINA.Core.Enum.LogLevelEnum.DEBUG);
    }
}
