namespace ExchangeIntegration.Exchange;

/// <summary>
/// Why an Exchange connectivity check did not succeed. The numeric values double as process exit codes and are
/// documented in the README, so they must stay stable.
/// </summary>
public enum FailureCategory
{
    /// <summary>The check succeeded.</summary>
    None = 0,

    /// <summary>An error the probe did not anticipate.</summary>
    Unexpected = 1,

    /// <summary>The Exchange settings are invalid; nothing was sent.</summary>
    Configuration = 2,

    /// <summary>The endpoint could not be reached (DNS, TCP, proxy).</summary>
    Connectivity = 3,

    /// <summary>TLS negotiation or server certificate validation failed.</summary>
    TlsFailure = 4,

    /// <summary>HTTP 401: the Windows credentials were not accepted.</summary>
    Authentication = 5,

    /// <summary>HTTP 403: refused before mailbox permissions were evaluated.</summary>
    HttpAccessDenied = 6,

    /// <summary>EWS answered but denied access to the mailbox (for example ErrorAccessDenied).</summary>
    MailboxAuthorization = 7,

    /// <summary>EWS could not resolve the mailbox (for example ErrorNonExistentMailbox).</summary>
    MailboxResolution = 8,

    /// <summary>The response was not a valid EWS SOAP response.</summary>
    SoapProtocol = 9,

    /// <summary>No complete response arrived within the configured timeout.</summary>
    Timeout = 10,

    /// <summary>The endpoint answered with a redirect, which is never followed.</summary>
    UnexpectedRedirect = 11,

    /// <summary>An HTTP status that is not an EWS response (404, 5xx, 407 ...).</summary>
    UnexpectedHttpStatus = 12,

    /// <summary>EWS returned some other error response code.</summary>
    EwsError = 13,

    /// <summary>The operator cancelled the probe.</summary>
    Cancelled = 130,
}
