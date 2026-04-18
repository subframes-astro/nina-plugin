using Subframes.NinaPlugin.Api;

namespace Subframes.NinaPlugin;

/// <summary>
/// Builds the <see cref="EventRequest"/> payload for an autofocus completion event.
///
/// Extracted as a static helper so the payload construction logic can be unit-tested
/// independently of NINA SDK types. <see cref="SessionService.UpdateEndAutoFocusRun"/>
/// calls this method and passes the result to <c>SubframesClient.PostEventAsync</c>.
/// </summary>
internal static class AutofocusEventBuilder
{
    /// <summary>
    /// Constructs an <see cref="EventRequest"/> for an autofocus completion event.
    /// </summary>
    /// <param name="sessionId">Active session ID assigned by the Subframes backend.</param>
    /// <param name="filter">Name of the filter in use during autofocus, or null when unknown.</param>
    /// <param name="temperature">Ambient temperature in °C at the time of the run, or null when unavailable.</param>
    /// <param name="position">Final focuser step position after the run.</param>
    /// <returns>An <see cref="EventRequest"/> ready to be passed to <c>PostEventAsync</c>.</returns>
    internal static EventRequest Build(
        string sessionId,
        string? filter,
        double? temperature,
        int position)
    {
        return new EventRequest
        {
            SessionId = sessionId,
            EventType = "autofocus",
            Timestamp = DateTime.UtcNow.ToString("o"),
            Metadata  = new Dictionary<string, object?>
            {
                ["filter"]      = filter,
                ["temperature"] = temperature,
                ["position"]    = position,
            },
        };
    }
}
