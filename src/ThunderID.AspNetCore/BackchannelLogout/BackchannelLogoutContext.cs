using Microsoft.AspNetCore.Http;

namespace ThunderID.AspNetCore.BackchannelLogout;

/// <summary>
/// A session ThunderID ended, passed to the back-channel logout callback. How many local sessions
/// it ends isn't known here: a cookie session ends on its next request.
/// </summary>
public sealed class BackchannelLogoutContext
{
    internal BackchannelLogoutContext(HttpContext httpContext, string? sessionId, string? subject)
    {
        HttpContext = httpContext;
        SessionId = sessionId;
        Subject = subject;
    }

    /// <summary>The back-channel request, for its services and <c>RequestAborted</c>.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>The ended session's <c>sid</c>, or null when the logout token named only the user.</summary>
    public string? SessionId { get; }

    /// <summary>The user's <c>sub</c>, or null when the logout token named only the session.</summary>
    public string? Subject { get; }
}
