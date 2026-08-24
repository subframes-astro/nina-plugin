namespace Subframes.NinaPlugin.Guiding;

/// <summary>
/// Minimal session context required by the guide-sample subsystem.
/// Implemented by <see cref="Subframes.NinaPlugin.SessionService"/> in production
/// and by test stubs in unit tests.
/// </summary>
public interface ISessionContext
{
    /// <summary>The Subframes session ID currently in progress, or <c>null</c> if no session is active.</summary>
    string? ActiveSessionId { get; }

    /// <summary><c>true</c> if there is an active session the plugin can attach telemetry to.</summary>
    bool HasActiveSession { get; }
}
