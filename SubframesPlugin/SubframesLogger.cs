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
/// mid-session, file logging will start immediately; if they change back, any queued
/// entries are flushed and file logging stops.
/// </para>
///
/// <para>
/// Mid-session level changes are detected via two complementary mechanisms:
/// <list type="number">
///   <item>NLog's <c>LogManager.ConfigurationChanged</c> event (subscribed via reflection)
///   for immediate, zero-latency detection whenever NINA reconfigures its loggers.</item>
///   <item>A 5-second fallback poll of <c>IsDebugEnabled</c> via reflection, covering edge
///   cases where NINA changes the threshold without triggering <c>ConfigurationChanged</c>.</item>
/// </list>
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
    // We access NINA's actual private NLog logger field (not a newly-created logger)
    // so that IsDebugEnabled reflects the exact same configuration that NINA itself uses.
    private static FieldInfo? _cachedNLogLoggerField;
    private static PropertyInfo? _isDebugEnabledProp;
    private static bool _reflectionResolved;

    // NLog ConfigurationChanged event subscription (for immediate level-change detection).
    private static EventInfo? _nlogConfigChangedEvent;
    private static Delegate? _nlogConfigChangedHandler;
    private static bool _eventSubscribed;

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

            // Subscribe to NLog's ConfigurationChanged event for immediate detection.
            // This fires whenever NINA calls LogManager.ReconfigExistingLoggers() or
            // modifies logging rules — the exact mechanism NINA uses when the user
            // changes the log level in preferences.
            TrySubscribeNLogConfigurationChanged();

            // Fallback: periodically re-check every 5 seconds to catch any edge cases
            // where NINA changes the threshold without triggering ConfigurationChanged.
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

            // Unsubscribe from NLog's ConfigurationChanged event.
            TryUnsubscribeNLogConfigurationChanged();

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

    // ── NLog event subscription helpers ────────────────────────────────────

    /// <summary>
    /// Attempts to subscribe to NLog's <c>LogManager.ConfigurationChanged</c> static event
    /// via reflection. This event fires immediately when NINA reconfigures its loggers
    /// (e.g. when the user changes the log level in NINA's preferences), giving us
    /// zero-latency detection without relying solely on the fallback polling timer.
    ///
    /// <para>Safe to call if reflection fails — the fallback poll timer covers the gap.</para>
    /// </summary>
    private static void TrySubscribeNLogConfigurationChanged()
    {
        try
        {
            if (_eventSubscribed)
                return;

            // NLog must already be loaded into the AppDomain by the time Initialize() runs.
            var nlogAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "NLog");
            if (nlogAssembly == null)
                return;

            var logManagerType = nlogAssembly.GetType("NLog.LogManager");
            if (logManagerType == null)
                return;

            _nlogConfigChangedEvent = logManagerType.GetEvent("ConfigurationChanged");
            if (_nlogConfigChangedEvent == null)
                return;

            // The event is EventHandler<LoggingConfigurationChangedEventArgs>.
            // We need a delegate whose signature matches (object?, EventArgs-derived).
            // Use GetMethod to locate our static handler — its second parameter is
            // declared as `object?` (EventArgs) which .NET allows for covariant
            // event handler substitution when we create the delegate dynamically.
            var handlerType = _nlogConfigChangedEvent.EventHandlerType;
            if (handlerType == null)
                return;

            var handlerMethod = typeof(SubframesLogger).GetMethod(
                nameof(OnNLogConfigurationChanged),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (handlerMethod == null)
                return;

            // Locate our static handler. Its second parameter is declared as
            // `EventArgs` (base class of LoggingConfigurationChangedEventArgs),
            // which satisfies .NET delegate parameter contravariance.
            // Delegate.CreateDelegate returns null (via throwOnBindFailure:false)
            // if the runtime rejects the signature — the poll timer then covers us.
            _nlogConfigChangedHandler = Delegate.CreateDelegate(handlerType, handlerMethod,
                throwOnBindFailure: false);
            if (_nlogConfigChangedHandler == null)
                return;

            _nlogConfigChangedEvent.AddEventHandler(null, _nlogConfigChangedHandler);
            _eventSubscribed = true;
        }
        catch
        {
            // Subscription is best-effort. Fallback poll timer will cover us.
            _eventSubscribed = false;
        }
    }

    /// <summary>Removes the NLog ConfigurationChanged subscription on shutdown.</summary>
    private static void TryUnsubscribeNLogConfigurationChanged()
    {
        try
        {
            if (_eventSubscribed && _nlogConfigChangedEvent != null && _nlogConfigChangedHandler != null)
            {
                _nlogConfigChangedEvent.RemoveEventHandler(null, _nlogConfigChangedHandler);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _eventSubscribed = false;
            _nlogConfigChangedHandler = null;
            _nlogConfigChangedEvent = null;
        }
    }

    /// <summary>
    /// Called by the NLog ConfigurationChanged event when NINA reconfigures its loggers.
    /// Signature must be compatible with <c>EventHandler&lt;LoggingConfigurationChangedEventArgs&gt;</c>.
    /// </summary>
    private static void OnNLogConfigurationChanged(object? sender, EventArgs e)
    {
        // Re-evaluate immediately on any NLog configuration change.
        EvaluateLogLevel();
    }

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
    /// We cannot reference NLog types directly (doing so would introduce a hard assembly
    /// dependency that breaks plugin loading via MEF). Instead we use reflection to access
    /// NINA's <em>actual</em> private NLog logger instance at runtime — the same object that
    /// successfully writes DEBUG messages to the NINA log — so that <c>IsDebugEnabled</c>
    /// is guaranteed to reflect the real active configuration.
    /// </para>
    /// <para>
    /// Previous approaches used <c>LogManager.GetLogger(name)</c> which returns a <em>new</em>
    /// logger instance whose level depends on NLog rule name-matching. If NINA's rules don't
    /// match the name we supply (e.g. because NINA uses <c>GlobalThreshold</c> instead of
    /// per-rule levels, or a different logger name), those loggers always report INFO and the
    /// check incorrectly returns false even when DEBUG output is being produced.
    /// </para>
    /// </summary>
    private static bool IsNinaDebugLevelEnabled()
    {
        try
        {
            if (!_reflectionResolved)
            {
                ResolveNLogReflectionHandles();
                _reflectionResolved = true;
            }

            // Primary path: read IsDebugEnabled from NINA's actual logger instance.
            if (_cachedNLogLoggerField != null && _isDebugEnabledProp != null)
            {
                var nlogLogger = _cachedNLogLoggerField.GetValue(null);
                return nlogLogger != null && (bool)(_isDebugEnabledProp.GetValue(nlogLogger) ?? false);
            }

            // Fallback path: query NLog's configuration directly.
            return IsFallbackDebugEnabled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves and caches the FieldInfo pointing to NINA's internal NLog logger field
    /// and the PropertyInfo for <c>IsDebugEnabled</c> on that logger instance.
    /// <para>
    /// Strategy:
    /// <list type="number">
    ///   <item>Try well-known private static field names: "logger", "_logger", "nlogger", "_nlogger", "log", "_log".</item>
    ///   <item>If none match, enumerate all private static fields on <c>NINA.Core.Utility.Logger</c>
    ///   and pick the first one whose declared type comes from the NLog assembly and is named "Logger".</item>
    /// </list>
    /// If neither strategy finds the field, <c>_cachedNLogLoggerField</c> remains null and
    /// <see cref="IsFallbackDebugEnabled"/> is used instead.
    /// </para>
    /// </summary>
    private static void ResolveNLogReflectionHandles()
    {
        try
        {
            var ninaLoggerType = typeof(NINA.Core.Utility.Logger);

            // Strategy 1: try well-known private static field names (most NINA versions).
            string[] candidateNames = { "logger", "_logger", "nlogger", "_nlogger", "log", "_log" };
            FieldInfo? loggerField = null;

            foreach (var name in candidateNames)
            {
                var f = ninaLoggerType.GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.IgnoreCase);
                if (f != null && IsNLogLoggerFieldType(f.FieldType))
                {
                    loggerField = f;
                    break;
                }
            }

            // Strategy 2: enumerate all private static fields and find one from NLog.
            if (loggerField == null)
            {
                var allStaticFields = ninaLoggerType.GetFields(BindingFlags.NonPublic | BindingFlags.Static);
                loggerField = allStaticFields.FirstOrDefault(f => IsNLogLoggerFieldType(f.FieldType));
            }

            if (loggerField == null)
                return; // No NLog field found — fall through to IsFallbackDebugEnabled.

            // Verify the field actually holds a non-null instance at resolution time.
            var testInstance = loggerField.GetValue(null);
            if (testInstance == null)
                return;

            var debugProp = testInstance.GetType().GetProperty("IsDebugEnabled");
            if (debugProp == null)
                return;

            _cachedNLogLoggerField = loggerField;
            _isDebugEnabledProp = debugProp;
        }
        catch
        {
            // Resolution failed — IsFallbackDebugEnabled will handle detection.
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the field type is NLog's Logger class (or a subclass),
    /// identified by assembly name and type name to avoid a direct NLog type reference.
    /// </summary>
    private static bool IsNLogLoggerFieldType(Type t)
    {
        return t.Assembly.GetName().Name == "NLog"
            && (t.Name == "Logger" || t.FullName == "NLog.Logger");
    }

    /// <summary>
    /// Fallback level check used when <see cref="ResolveNLogReflectionHandles"/> could not
    /// locate NINA's internal logger field.
    /// <para>
    /// Checks in order: <c>LogManager.GlobalThreshold</c> ordinal (Trace=0, Debug=1 ≤ 1 means
    /// debug enabled), then iterates <c>LogManager.Configuration.LoggingRules</c> and calls
    /// <c>IsLoggingEnabledForLevel(NLog.LogLevel.Debug)</c> on each rule.
    /// </para>
    /// </summary>
    private static bool IsFallbackDebugEnabled()
    {
        try
        {
            var nlogAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "NLog");
            if (nlogAssembly == null)
                return false;

            var logManagerType = nlogAssembly.GetType("NLog.LogManager");
            if (logManagerType == null)
                return false;

            // Check GlobalThreshold first — if it's set to Debug or Trace, we're done.
            var globalThresholdProp = logManagerType.GetProperty("GlobalThreshold",
                BindingFlags.Public | BindingFlags.Static);
            if (globalThresholdProp != null)
            {
                var threshold = globalThresholdProp.GetValue(null);
                if (threshold != null)
                {
                    var ordinalProp = threshold.GetType().GetProperty("Ordinal");
                    if (ordinalProp != null)
                    {
                        int ordinal = (int)(ordinalProp.GetValue(threshold) ?? 6);
                        // NLog ordinals: Trace=0, Debug=1, Info=2, Warn=3, Error=4, Fatal=5, Off=6
                        if (ordinal <= 1)
                            return true;
                        // If GlobalThreshold is explicitly Info or higher, bail out early.
                        if (ordinal > 1 && ordinal < 6)
                            return false;
                        // ordinal == 6 (Off) means GlobalThreshold is not restricting — fall through.
                    }
                }
            }

            // Fall through to per-rule check.
            var configProp = logManagerType.GetProperty("Configuration",
                BindingFlags.Public | BindingFlags.Static);
            if (configProp == null)
                return false;

            var config = configProp.GetValue(null);
            if (config == null)
                return false;

            var rulesProp = config.GetType().GetProperty("LoggingRules");
            if (rulesProp == null)
                return false;

            var rules = rulesProp.GetValue(config) as System.Collections.IEnumerable;
            if (rules == null)
                return false;

            // Resolve NLog.LogLevel.Debug value once.
            var debugLevel = nlogAssembly.GetType("NLog.LogLevel")
                ?.GetField("Debug", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (debugLevel == null)
                return false;

            foreach (var rule in rules)
            {
                var isEnabledMethod = rule.GetType().GetMethod("IsLoggingEnabledForLevel");
                if (isEnabledMethod != null)
                {
                    var result = isEnabledMethod.Invoke(rule, new[] { debugLevel });
                    if (result is bool enabled && enabled)
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
