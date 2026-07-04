using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Subframes.NinaPlugin;

/// <summary>
/// Static logging wrapper for the Subframes plugin.
/// <list type="bullet">
///   <item>Always forwards all messages to NINA's global <c>Logger</c> with a
///   <c>[Subframes]</c> prefix — NINA's NLog rules filter by level as normal.</item>
///   <item>When NINA's log level is Debug or Trace, also writes to a dedicated
///   log file at <c>%LOCALAPPDATA%\NINA\Logs\subframes-{launchTimestamp}.log</c> that
///   captures <em>all</em> Subframes messages regardless of level.</item>
/// </list>
///
/// <para>
/// The debug log file is created once per NINA launch (not per calendar day), matching
/// NINA's own log file behaviour. The logger dynamically monitors NINA's global log level
/// and enables/disables file logging accordingly — if the user changes from INFO to DEBUG
/// mid-session, file logging will start; if they change back, it will stop.
/// </para>
///
/// Call <see cref="Initialize"/> from <c>Plugin.OnImportsSatisfied()</c> and
/// <see cref="Shutdown"/> from <c>Plugin.Teardown()</c>.
///
/// <para>
/// <b>Design note:</b> This implementation uses plain file I/O instead of NLog's
/// FileTarget API to avoid a direct assembly dependency on NLog. NINA provides NLog
/// at runtime, but MEF composition fails if the plugin assembly metadata references
/// NLog types directly (the plugin directory doesn't contain NLog.dll). By using only
/// NINA.Core.Utility.Logger (which wraps NLog internally), we keep the indirect
/// reference pattern that NINA's plugin loader expects.
/// </para>
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

    // Cached reflection handles for NLog level checking (resolved once).
    private static MethodInfo? _getLoggerMethod;
    private static PropertyInfo? _isDebugEnabledProp;
    private static bool _reflectionResolved;

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

            // Periodically re-check NINA's log level every 10 seconds to adapt dynamically.
            _levelCheckTimer = new Timer(_ => EvaluateLogLevel(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

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
                NINA.Core.Utility.Logger.Info($"[Subframes] Debug log file enabled: {_logFilePath}");
            }
            else if (!debugEnabled && _fileLoggingActive)
            {
                _fileLoggingActive = false;
                NINA.Core.Utility.Logger.Info("[Subframes] Debug log file disabled — NINA log level changed to INFO or higher.");
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
    /// Returns <c>true</c> when NINA's log configuration currently enables the Debug level.
    /// <para>
    /// We cannot reference NLog types directly (doing so would introduce a hard assembly
    /// dependency that breaks plugin loading via MEF). Instead we use reflection to check
    /// NLog's LogManager at runtime — by then NINA has already loaded NLog into the AppDomain.
    /// Reflection handles are cached after first resolution for performance.
    /// </para>
    /// </summary>
    private static bool IsNinaDebugLevelEnabled()
    {
        try
        {
            // Resolve reflection handles once and cache them.
            if (!_reflectionResolved)
            {
                var nlogAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "NLog");
                if (nlogAssembly == null)
                    return false;

                var logManagerType = nlogAssembly.GetType("NLog.LogManager");
                if (logManagerType == null)
                    return false;

                _getLoggerMethod = logManagerType.GetMethod("GetLogger", new[] { typeof(string) });
                if (_getLoggerMethod == null)
                    return false;

                // Get the property type from the logger instance.
                var tempLogger = _getLoggerMethod.Invoke(null, new object[] { "NINA" });
                if (tempLogger == null)
                    return false;

                _isDebugEnabledProp = tempLogger.GetType().GetProperty("IsDebugEnabled");
                _reflectionResolved = true;
            }

            if (_getLoggerMethod == null || _isDebugEnabledProp == null)
                return false;

            var logger = _getLoggerMethod.Invoke(null, new object[] { "NINA" });
            if (logger == null)
                return false;

            return (bool)(_isDebugEnabledProp.GetValue(logger) ?? false);
        }
        catch
        {
            return false;
        }
    }
}
