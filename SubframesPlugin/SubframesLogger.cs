using System;
using System.IO;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Subframes.NinaPlugin;

/// <summary>
/// Static logging wrapper for the Subframes plugin.
/// <list type="bullet">
///   <item>Always forwards all messages to NINA's global <c>Logger</c> with a
///   <c>[Subframes]</c> prefix — NINA's NLog rules filter by level as normal.</item>
///   <item>When NINA's log level is Debug or Trace, also creates a dedicated NLog
///   <c>FileTarget</c> at <c>%LOCALAPPDATA%\NINA\Logs\subframes-{date}.log</c> that captures
///   <em>all</em> Subframes messages regardless of level.</item>
/// </list>
/// Call <see cref="Initialize"/> from <c>Plugin.OnImportsSatisfied()</c> and
/// <see cref="Shutdown"/> from <c>Plugin.Teardown()</c>.
/// </summary>
public static class SubframesLogger
{
    // Logger name used for the dedicated NLog file rule.
    private const string LoggerName = "Subframes.NinaPlugin";

    // Name under which we register the async wrapper in the NLog config
    // so we can remove it cleanly on shutdown.
    private const string AsyncTargetName = "SubframesFileAsync";

    private static NLog.Logger? _fileLogger;

    // Volatile so reads from any thread see the latest write without a lock.
    private static volatile bool _fileLoggingActive;

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the dedicated Subframes log file when NINA's NLog configuration has
    /// Debug or Trace level enabled.  Safe to call multiple times — subsequent calls are
    /// no-ops if the file target is already active.
    /// </summary>
    public static void Initialize()
    {
        if (_fileLoggingActive)
            return;

        try
        {
            if (!IsNinaDebugLevelEnabled())
            {
                NINA.Core.Utility.Logger.Info("[Subframes] Debug log file inactive — NINA log level is INFO or higher.");
                return;
            }

            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Logs");
            Directory.CreateDirectory(logDir);

            // Use NLog's ${shortdate} layout renderer so the file rolls daily
            // without us having to manage the filename ourselves.
            string filePath = Path.Combine(logDir, "subframes-${shortdate}.log");

            var fileTarget = new FileTarget("SubframesFile")
            {
                FileName = filePath,
                // Format: 2026-07-03 22:15:33.4567 | DEBUG | [Subframes] message
                Layout = "${longdate} | ${level:uppercase=true:padding=5} | [Subframes] ${message}"
                       + "${onexception:inner=${newline}${exception:format=tostring}}",
                // Safety settings required for a plugin that shares a process with NINA.
                KeepFileOpen = false,
                ConcurrentWrites = true,
                // Daily rolling archive, 30-day retention.
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveNumbering = ArchiveNumberingMode.Date,
                MaxArchiveDays = 30,
            };

            // Wrap in an async target so file I/O never blocks the imaging thread.
            var asyncTarget = new AsyncTargetWrapper(fileTarget)
            {
                Name = AsyncTargetName,
                OverflowAction = AsyncTargetWrapperOverflowAction.Discard,
            };

            // Obtain (or create) the current NLog configuration and add our rule.
            // Mark the rule Final so messages for our logger do NOT propagate to
            // NINA's own file/UI targets — we call NINA.Core.Utility.Logger directly for that.
            var config = LogManager.Configuration ?? new LoggingConfiguration();
            config.AddTarget(asyncTarget);

            var rule = new LoggingRule(LoggerName + "*", LogLevel.Trace, asyncTarget);
            rule.SetLoggingLevels(LogLevel.Trace, LogLevel.Fatal);
            rule.Final = true;
            // Insert at position 0 so our specific rule is evaluated before the
            // catch-all "*" rules that ship with NINA's NLog config.
            config.LoggingRules.Insert(0, rule);

            LogManager.Configuration = config;

            _fileLogger = LogManager.GetLogger(LoggerName);
            _fileLoggingActive = true;

            NINA.Core.Utility.Logger.Info($"[Subframes] Debug log file enabled: {logDir}");
        }
        catch (Exception ex)
        {
            _fileLoggingActive = false;
            NINA.Core.Utility.Logger.Warning($"[Subframes] Failed to initialise debug log file: {ex.Message}");
        }
    }

    /// <summary>
    /// Flushes and removes the dedicated Subframes log file target from NLog.
    /// Safe to call even when <see cref="Initialize"/> was never called.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _fileLoggingActive = false;
            _fileLogger = null;

            if (LogManager.Configuration is { } cfg)
            {
                var toRemove = cfg.LoggingRules
                    .Where(r => r.LoggerNamePattern.StartsWith(LoggerName,
                                StringComparison.Ordinal))
                    .ToList();
                foreach (var r in toRemove)
                    cfg.LoggingRules.Remove(r);

                cfg.RemoveTarget(AsyncTargetName);
                LogManager.ReconfigExistingLoggers();
            }
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
        if (_fileLoggingActive) _fileLogger?.Info(message);
    }

    /// <summary>Logs a DEBUG message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Debug(string message)
    {
        NINA.Core.Utility.Logger.Debug($"[Subframes] {message}");
        if (_fileLoggingActive) _fileLogger?.Debug(message);
    }

    /// <summary>Logs a WARNING message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Warning(string message)
    {
        NINA.Core.Utility.Logger.Warning($"[Subframes] {message}");
        if (_fileLoggingActive) _fileLogger?.Warn(message);
    }

    /// <summary>Logs an ERROR message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Error(string message)
    {
        NINA.Core.Utility.Logger.Error($"[Subframes] {message}");
        if (_fileLoggingActive) _fileLogger?.Error(message);
    }

    /// <summary>Logs a TRACE message to NINA's log and (when active) to the Subframes log file.</summary>
    public static void Trace(string message)
    {
        NINA.Core.Utility.Logger.Trace($"[Subframes] {message}");
        if (_fileLoggingActive) _fileLogger?.Trace(message);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when NINA's NLog configuration currently enables the Debug level,
    /// which means the user has set NINA's log level to Debug or Trace.
    /// </summary>
    private static bool IsNinaDebugLevelEnabled()
    {
        try
        {
            // NINA uses NLog internally under the logger name "NINA".
            // If that logger has Debug enabled, NINA is running in verbose mode.
            return LogManager.GetLogger("NINA").IsDebugEnabled;
        }
        catch
        {
            return false;
        }
    }
}
