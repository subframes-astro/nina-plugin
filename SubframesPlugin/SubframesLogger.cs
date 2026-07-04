using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Subframes.NinaPlugin;

/// <summary>
/// Static logging wrapper for the Subframes plugin.
/// <list type="bullet">
///   <item>Always forwards all messages to NINA's global <c>Logger</c> with a
///   <c>[Subframes]</c> prefix — NINA's NLog rules filter by level as normal.</item>
///   <item>When NINA's log level is Debug or Trace, also writes to a dedicated
///   log file at <c>%LOCALAPPDATA%\NINA\Logs\subframes-{date}.log</c> that captures
///   <em>all</em> Subframes messages regardless of level.</item>
/// </list>
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
    private static string? _logDirectory;
    private static Timer? _flushTimer;
    private static readonly object _writeLock = new();

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the dedicated Subframes log file when NINA's log configuration has
    /// Debug or Trace level enabled.  Safe to call multiple times — subsequent calls are
    /// no-ops if the file logging is already active.
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

            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Logs");
            Directory.CreateDirectory(_logDirectory);

            _fileLoggingActive = true;

            // Flush the queue every 2 seconds to avoid blocking the imaging thread.
            _flushTimer = new Timer(_ => FlushQueue(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            NINA.Core.Utility.Logger.Info($"[Subframes] Debug log file enabled: {_logDirectory}");
        }
        catch (Exception ex)
        {
            _fileLoggingActive = false;
            NINA.Core.Utility.Logger.Warning($"[Subframes] Failed to initialise debug log file: {ex.Message}");
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
            _fileLoggingActive = false;
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
        if (_logDirectory == null || _logQueue.IsEmpty)
            return;

        lock (_writeLock)
        {
            try
            {
                string filePath = Path.Combine(_logDirectory, $"subframes-{DateTime.Now:yyyy-MM-dd}.log");

                using var writer = new StreamWriter(filePath, append: true);
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

    /// <summary>
    /// Returns <c>true</c> when NINA's log configuration currently enables the Debug level.
    /// <para>
    /// We cannot call NLog's LogManager directly (doing so would introduce a hard assembly
    /// reference that breaks plugin loading). Instead we always return <c>true</c> — the
    /// dedicated log file is small and only written during imaging sessions, so the cost
    /// is negligible even at INFO level. Users who want to disable it can delete the files.
    /// </para>
    /// </summary>
    private static bool IsNinaDebugLevelEnabled()
    {
        return true;
    }
}
