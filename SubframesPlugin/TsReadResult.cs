namespace Subframes.NinaPlugin;

/// <summary>
/// Outcome of a best-effort Target Scheduler (TS) SQLite read.
///
/// TS readers must never throw and must never block callers, but a plain
/// <c>null</c> return value used to conflate three very different situations:
/// TS isn't installed, TS is installed but genuinely has no matching rows, and
/// TS is installed but the DB could not be read (locked by another process,
/// renamed, moved, or a schema change from a TS upgrade). From the user's
/// point of view the third case looks identical to "Subframes isn't talking
/// to Target Scheduler" — this type lets readers report which one actually
/// happened so the backend/UI can say what's wrong instead of guessing.
/// </summary>
internal enum TsReadStatus
{
    /// <summary>Read succeeded. Data may still be empty (e.g. no active projects tonight).</summary>
    Ok,

    /// <summary>Target Scheduler is not installed / its database file does not exist on disk.</summary>
    NotInstalled,

    /// <summary>
    /// TS appears to be installed (its DB file exists) but the read failed —
    /// locked file, permissions, corrupt/renamed DB, or a schema change.
    /// </summary>
    Error,
}

/// <summary>
/// Wraps the result of a TS SQLite read with enough detail to distinguish
/// "no TS data" from "TS read failed" at the call site and, ultimately, in the
/// payload sent to the backend.
/// </summary>
internal readonly struct TsReadResult<T>
{
    public TsReadStatus Status { get; }
    public T? Data { get; }

    /// <summary>Exception type name when <see cref="Status"/> is <see cref="TsReadStatus.Error"/>; otherwise null.</summary>
    public string? ErrorType { get; }

    /// <summary>Exception message when <see cref="Status"/> is <see cref="TsReadStatus.Error"/>; otherwise null.</summary>
    public string? ErrorMessage { get; }

    private TsReadResult(TsReadStatus status, T? data, string? errorType, string? errorMessage)
    {
        Status = status;
        Data = data;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
    }

    public static TsReadResult<T> Ok(T? data) => new(TsReadStatus.Ok, data, null, null);

    public static TsReadResult<T> NotInstalled() => new(TsReadStatus.NotInstalled, default, null, null);

    public static TsReadResult<T> Error(Exception ex) =>
        new(TsReadStatus.Error, default, ex.GetType().Name, ex.Message);

    /// <summary>Wire form for API payloads: <c>"ok"</c> | <c>"not_installed"</c> | <c>"error"</c>.</summary>
    public string ToWireStatus() => Status switch
    {
        TsReadStatus.Ok           => "ok",
        TsReadStatus.NotInstalled => "not_installed",
        TsReadStatus.Error        => "error",
        _                         => "ok",
    };

    /// <summary>Max length of the string returned by <see cref="ToWireError"/>, including the ellipsis marker on overflow.</summary>
    internal const int MaxWireErrorLength = 256;

    private const string Ellipsis = "...";

    /// <summary>
    /// Short, safe-to-send error summary, e.g. "SqliteException: database is locked". Null unless
    /// Status is Error. Truncated to <see cref="MaxWireErrorLength"/> characters (with a trailing
    /// "..." marker on overflow) so a pathological exception message can't blow up the payload
    /// sent to the backend on every heartbeat while a TS read is failing.
    /// </summary>
    public string? ToWireError()
    {
        if (Status != TsReadStatus.Error)
        {
            return null;
        }

        var combined = $"{ErrorType}: {ErrorMessage}";
        if (combined.Length <= MaxWireErrorLength)
        {
            return combined;
        }

        return combined[..(MaxWireErrorLength - Ellipsis.Length)] + Ellipsis;
    }
}
