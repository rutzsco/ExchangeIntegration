using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Diagnostics;

/// <summary>What one stage of the probe established.</summary>
public enum CheckStatus
{
    /// <summary>The stage was never reached.</summary>
    NotEvaluated = 0,

    Success = 1,

    /// <summary>The stage was reached but the evidence neither proves success nor failure.</summary>
    NotConfirmed = 2,

    Failed = 3,
}

/// <summary>Where the next troubleshooting step most likely belongs. A failure can point at more than one area.</summary>
[Flags]
public enum TroubleshootingArea
{
    None = 0,
    Configuration = 1,
    Network = 2,
    WindowsIdentity = 4,
    ExchangeAuthentication = 8,
    MailboxPermissions = 16,
    SoapImplementation = 32,
    ExchangeService = 64,
    Application = 128,
}

/// <summary>The evaluated outcome of one probe run. Holds no identifiers, tokens or raw responses.</summary>
public sealed class ConnectivityResult
{
    public required string WindowsIdentity { get; init; }

    public ExchangeAccessMode? AccessMode { get; init; }

    /// <summary>Endpoint reduced to scheme, host and path.</summary>
    public string? Endpoint { get; init; }

    public string? Mailbox { get; init; }

    public string EwsOperation { get; init; } = EwsClient.GetFolderOperation;

    public required FailureCategory Category { get; init; }

    public CheckStatus HttpConnection { get; init; }

    public CheckStatus WindowsAuthentication { get; init; }

    public CheckStatus MailboxAccess { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? EwsResponseCode { get; init; }

    /// <summary>Sanitized explanation of the outcome.</summary>
    public string? Detail { get; init; }

    /// <summary>Short one-way fingerprint of the Inbox identifier; the identifier itself is never carried.</summary>
    public string? FolderIdFingerprint { get; init; }

    public string? ServerVersion { get; init; }

    public IReadOnlyList<string> OfferedAuthenticationSchemes { get; init; } = [];

    public IReadOnlyList<string> ConfigurationErrors { get; init; } = [];

    public TroubleshootingArea LikelyArea { get; init; }

    public string? NextStep { get; init; }

    public bool IsSuccess => Category == FailureCategory.None;

    /// <summary>The process exit code: 0 on success, otherwise the numeric value of <see cref="Category"/>.</summary>
    public int ExitCode => (int)Category;

    /// <summary>A result for settings that failed validation; nothing was sent to Exchange.</summary>
    public static ConnectivityResult ForConfigurationFailure(string windowsIdentity, IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new ConnectivityResult
        {
            WindowsIdentity = windowsIdentity,
            Category = FailureCategory.Configuration,
            ConfigurationErrors = errors.Select(Redaction.SanitizeMessage).ToArray(),
            LikelyArea = TroubleshootingArea.Configuration,
            NextStep = ExchangeConnectivityProbe.GetNextStep(FailureCategory.Configuration),
        };
    }
}
