namespace ExchangeIntegration.Configuration;

/// <summary>How the service identity reaches the target mailbox.</summary>
public enum ExchangeAccessMode
{
    /// <summary>The Windows identity of the process holds mailbox or folder permissions on the target mailbox.</summary>
    Delegated = 0,

    /// <summary>Reserved for scoped Exchange impersonation in a later milestone; rejected by validation for now.</summary>
    Impersonation = 1,
}

/// <summary>Exchange settings bound from the "Exchange" configuration section.</summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Exchange";
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 300;
    public const int DefaultPollIntervalSeconds = 60;
    public const int DefaultMaxMessagesPerPoll = 20;
    public const string DefaultMoveFolder = "Processed";

    /// <summary>HTTPS URL of the EWS endpoint, for example https://mail.contoso.com/EWS/Exchange.asmx.</summary>
    public string EwsUrl { get; set; } = string.Empty;

    /// <summary>Primary SMTP address of the mailbox whose Inbox is probed.</summary>
    public string Mailbox { get; set; } = string.Empty;

    public ExchangeAccessMode AccessMode { get; set; } = ExchangeAccessMode.Delegated;

    /// <summary>Overall timeout for one EWS request, including the authentication handshake.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>Service mode: seconds between Inbox polls.</summary>
    public int PollIntervalSeconds { get; set; } = DefaultPollIntervalSeconds;

    /// <summary>Service mode: the most new emails captured in one poll; the rest wait for the next poll.</summary>
    public int MaxMessagesPerPoll { get; set; } = DefaultMaxMessagesPerPoll;

    /// <summary>
    /// Service mode: the only folders a move command may target, by display name, at the top level of the mailbox
    /// (beside the Inbox). Left empty, only "Processed" is allowed; see <see cref="GetMoveFolders"/>.
    /// </summary>
    public string[] MoveFolders { get; set; } = [];

    // The default is applied here rather than as an initial value because the binder appends to a non-empty array.
    public IReadOnlyList<string> GetMoveFolders() => MoveFolders is { Length: > 0 } ? MoveFolders : [DefaultMoveFolder];

    /// <summary>The endpoint reduced to scheme, host, port and path, so user info, query and fragment never reach output.</summary>
    public string GetDisplayEndpoint() =>
        Uri.TryCreate(EwsUrl, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "(invalid URL)";
}
