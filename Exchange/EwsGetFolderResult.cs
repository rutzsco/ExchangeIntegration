using System.Net;

namespace ExchangeIntegration.Exchange;

/// <summary>
/// Outcome of the read-only Inbox GetFolder request, already classified. This is a class rather than a record on
/// purpose: a record's generated ToString would print <see cref="FolderId"/> if the object were ever logged.
/// </summary>
public sealed class EwsGetFolderResult
{
    public required FailureCategory Category { get; init; }

    /// <summary>HTTP status of the response; null when no HTTP response was received.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>EWS ResponseClass (Success, Warning or Error) when a GetFolder response was parsed.</summary>
    public string? EwsResponseClass { get; init; }

    /// <summary>EWS ResponseCode, for example NoError or ErrorAccessDenied, when the server supplied one.</summary>
    public string? EwsResponseCode { get; init; }

    /// <summary>Sanitized, human-readable detail that is safe to log and print.</summary>
    public string? Message { get; init; }

    /// <summary>The Inbox identifier returned by Exchange. Never log it; use <see cref="Redaction.Fingerprint"/>.</summary>
    public string? FolderId { get; init; }

    public string? FolderDisplayName { get; init; }

    /// <summary>Exchange build that answered, from the ServerVersionInfo header (for example 15.2.2562.17).</summary>
    public string? ServerVersion { get; init; }

    /// <summary>Authentication scheme names (never tokens) the server offered with an HTTP 401.</summary>
    public IReadOnlyList<string> OfferedAuthenticationSchemes { get; init; } = [];

    public bool Succeeded => Category == FailureCategory.None;
}
