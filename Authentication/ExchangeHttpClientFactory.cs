using ExchangeIntegration.Configuration;

namespace ExchangeIntegration.Authentication;

/// <summary>
/// Creates the <see cref="HttpClient"/> used for EWS. Authentication is limited to the Windows identity of the
/// process (Negotiate or NTLM through default credentials): no user names, passwords or alternative credential
/// mechanisms are configured, redirects are never followed, and TLS certificate validation is left untouched.
/// </summary>
public static class ExchangeHttpClientFactory
{
    /// <summary>Sent on every request so administrators can recognize the probe in IIS logs and EWS access policies.</summary>
    public const string UserAgent = "ExchangeIntegration-EwsProbe/1.0";

    /// <summary>Sent by service mode (--run), so its traffic is distinguishable from the probe's.</summary>
    public const string AdapterUserAgent = "ExchangeIntegration-Adapter/1.0";

    /// <summary>EWS answers to a read-only GetFolder are tiny; refuse to buffer anything unreasonably large.</summary>
    public const long MaxResponseBytes = 1024 * 1024;

    /// <summary>Service mode downloads attachments as base64 inside SOAP, so it allows far larger responses.</summary>
    public const long AdapterMaxResponseBytes = 128L * 1024 * 1024;

    public static HttpClientHandler CreateHandler() => new()
    {
        UseDefaultCredentials = true,
        AllowAutoRedirect = false,
    };

    public static HttpClient Create(ExchangeOptions options) => Create(options, CreateHandler());

    /// <summary>The service-mode client: same Windows authentication and redirect policy, larger responses.</summary>
    public static HttpClient CreateForAdapter(ExchangeOptions options) =>
        Create(options, CreateHandler(), AdapterUserAgent, AdapterMaxResponseBytes);

    /// <summary>Applies the client-level settings to a caller-supplied handler (a mocked transport in tests).</summary>
    public static HttpClient Create(ExchangeOptions options, HttpMessageHandler handler) =>
        Create(options, handler, UserAgent, MaxResponseBytes);

    private static HttpClient Create(ExchangeOptions options, HttpMessageHandler handler, string userAgent, long maxResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxResponseContentBufferSize = maxResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }
}
