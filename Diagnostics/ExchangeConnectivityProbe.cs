using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Diagnostics;

/// <summary>
/// Runs the one-time, read-only connectivity check: one Inbox GetFolder against the configured mailbox using the
/// Windows identity of the process. A stage is only reported as successful when the evidence proves it; reaching
/// the endpoint is never taken as proof of authentication.
/// </summary>
public sealed class ExchangeConnectivityProbe
{
    private readonly IEwsClient _ewsClient;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly ExchangeOptions _options;
    private readonly ILogger<ExchangeConnectivityProbe> _logger;

    public ExchangeConnectivityProbe(
        IEwsClient ewsClient,
        IProcessIdentityProvider identityProvider,
        IOptions<ExchangeOptions> options,
        ILogger<ExchangeConnectivityProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(ewsClient);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _ewsClient = ewsClient;
        _identityProvider = identityProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ConnectivityResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var identity = _identityProvider.GetCurrentIdentityName();

        _logger.LogInformation(
            "Starting Exchange connectivity probe: WindowsIdentity={WindowsIdentity} AccessMode={AccessMode} Endpoint={Endpoint} Mailbox={Mailbox}",
            identity,
            _options.AccessMode,
            _options.GetDisplayEndpoint(),
            _options.Mailbox);

        EwsGetFolderResult ewsResult;
        try
        {
            ewsResult = await _ewsClient.GetInboxFolderAsync(_options.Mailbox, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Complete(identity, FailureCategory.Cancelled, "The probe was cancelled before Exchange answered.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The connectivity probe failed unexpectedly.");
            return Complete(identity, FailureCategory.Unexpected, $"{exception.GetType().Name}: {Redaction.SanitizeMessage(exception.Message)}");
        }

        var interpretation = Interpret(ewsResult.Category);
        return Complete(new ConnectivityResult
        {
            WindowsIdentity = identity,
            AccessMode = _options.AccessMode,
            Endpoint = _options.GetDisplayEndpoint(),
            Mailbox = _options.Mailbox,
            Category = ewsResult.Category,
            HttpConnection = interpretation.HttpConnection,
            WindowsAuthentication = interpretation.WindowsAuthentication,
            MailboxAccess = interpretation.MailboxAccess,
            HttpStatusCode = (int?)ewsResult.StatusCode,
            EwsResponseCode = ewsResult.EwsResponseCode,
            Detail = ewsResult.Message,
            FolderIdFingerprint = ewsResult.FolderId is null ? null : Redaction.Fingerprint(ewsResult.FolderId),
            ServerVersion = ewsResult.ServerVersion,
            OfferedAuthenticationSchemes = ewsResult.OfferedAuthenticationSchemes,
            LikelyArea = interpretation.Area,
            NextStep = interpretation.NextStep,
        });
    }

    /// <summary>Operator guidance for a failure category, shared with the configuration-failure result.</summary>
    public static string GetNextStep(FailureCategory category) => Interpret(category).NextStep;

    private ConnectivityResult Complete(string identity, FailureCategory category, string detail)
    {
        var interpretation = Interpret(category);
        return Complete(new ConnectivityResult
        {
            WindowsIdentity = identity,
            AccessMode = _options.AccessMode,
            Endpoint = _options.GetDisplayEndpoint(),
            Mailbox = _options.Mailbox,
            Category = category,
            HttpConnection = interpretation.HttpConnection,
            WindowsAuthentication = interpretation.WindowsAuthentication,
            MailboxAccess = interpretation.MailboxAccess,
            Detail = detail,
            LikelyArea = interpretation.Area,
            NextStep = interpretation.NextStep,
        });
    }

    private ConnectivityResult Complete(ConnectivityResult result)
    {
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Connectivity probe succeeded: WindowsIdentity={WindowsIdentity} EwsResponseCode={EwsResponseCode} FolderIdFingerprint={FolderIdFingerprint}",
                result.WindowsIdentity,
                result.EwsResponseCode,
                result.FolderIdFingerprint);
        }
        else
        {
            _logger.LogWarning(
                "Connectivity probe failed: Category={FailureCategory} WindowsAuthentication={WindowsAuthentication} MailboxAccess={MailboxAccess} LikelyArea={LikelyArea} ExitCode={ExitCode}",
                result.Category,
                result.WindowsAuthentication,
                result.MailboxAccess,
                result.LikelyArea,
                result.ExitCode);
        }

        return result;
    }

    private sealed record Interpretation(
        CheckStatus HttpConnection,
        CheckStatus WindowsAuthentication,
        CheckStatus MailboxAccess,
        TroubleshootingArea Area,
        string NextStep);

    // Windows authentication is only "Success" once EWS itself answered (any EWS response, even an error, is
    // produced for an authenticated caller). Statuses that arrive before that stay "NotConfirmed".
    private static Interpretation Interpret(FailureCategory category) => category switch
    {
        FailureCategory.None => new(
            CheckStatus.Success, CheckStatus.Success, CheckStatus.Success,
            TroubleshootingArea.None,
            string.Empty),

        FailureCategory.Configuration => new(
            CheckStatus.NotEvaluated, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.Configuration,
            "Correct the Exchange settings in appsettings.json or the Exchange__* environment variables and run the probe again. Nothing was sent to Exchange."),

        FailureCategory.Connectivity => new(
            CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.Network | TroubleshootingArea.Configuration,
            "The Exchange endpoint was not reached. Check that the EWS host name resolves from this machine, that TCP 443 is open through firewalls and proxies, and that Exchange:EwsUrl is correct."),

        FailureCategory.TlsFailure => new(
            CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.Network,
            "Do not disable certificate validation. Make sure this machine trusts the CA chain that issued the Exchange certificate and that Exchange:EwsUrl uses a host name the certificate covers."),

        FailureCategory.Timeout => new(
            CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.Network | TroubleshootingArea.ExchangeService,
            "No complete response arrived within Exchange:TimeoutSeconds. Check the network path, Exchange and IIS health, and domain controller reachability for authentication; raise the timeout only after ruling these out."),

        FailureCategory.Authentication => new(
            CheckStatus.Success, CheckStatus.Failed, CheckStatus.NotEvaluated,
            TroubleshootingArea.WindowsIdentity | TroubleshootingArea.ExchangeAuthentication,
            "Exchange did not accept the Windows credentials (HTTP 401). Confirm the identity shown above is the intended, enabled domain account, and ask the Exchange administrator to confirm Windows Integrated Authentication (Negotiate and NTLM) is enabled on the EWS virtual directory and, for Kerberos, that an SPN exists for the EWS host name. Basic authentication is intentionally never attempted."),

        FailureCategory.HttpAccessDenied => new(
            CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated,
            TroubleshootingArea.ExchangeAuthentication | TroubleshootingArea.ExchangeService,
            "HTTP 403: Exchange or IIS refused the request before mailbox permissions were evaluated. Ask the Exchange administrator to check EWS access policies (EwsEnabled, EwsApplicationAccessPolicy and allow or block lists against the User-Agent in the README), IIS IP restrictions, and any proxy or WAF in front of EWS."),

        FailureCategory.UnexpectedRedirect => new(
            CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated,
            TroubleshootingArea.Configuration | TroubleshootingArea.ExchangeAuthentication,
            "The endpoint redirected instead of answering, for example to a sign-in page or another host. Redirects are never followed because credentials must not be forwarded. Point Exchange:EwsUrl at the final EWS endpoint or ask the administrator why the EWS virtual directory redirects."),

        FailureCategory.UnexpectedHttpStatus => new(
            CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated,
            TroubleshootingArea.ExchangeService | TroubleshootingArea.Network,
            "The server answered with an HTTP status that is not an EWS response. Verify Exchange:EwsUrl points at /EWS/Exchange.asmx on the intended server and check Exchange and IIS health and any proxy in the path (HTTP 407 means a proxy requires authentication)."),

        FailureCategory.SoapProtocol => new(
            CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated,
            TroubleshootingArea.SoapImplementation | TroubleshootingArea.Configuration,
            "The response was not a valid EWS SOAP response. If Exchange:EwsUrl is right and other EWS clients work against it, the SOAP implementation here is the likely cause; otherwise look for a proxy, load balancer or sign-in page returning HTML."),

        FailureCategory.MailboxAuthorization => new(
            CheckStatus.Success, CheckStatus.Success, CheckStatus.Failed,
            TroubleshootingArea.MailboxPermissions,
            "Exchange authenticated this identity but denied access to the mailbox Inbox (EWS access denied). Ask the Exchange administrator to verify that the identity shown above holds delegate access to the mailbox, either Full Access or at least folder-level access to the Inbox."),

        FailureCategory.MailboxResolution => new(
            CheckStatus.Success, CheckStatus.Success, CheckStatus.Failed,
            TroubleshootingArea.MailboxPermissions | TroubleshootingArea.Configuration,
            "Exchange could not resolve the mailbox. Check that Exchange:Mailbox is the primary SMTP address of an existing, enabled mailbox, and ask the Exchange administrator whether this identity is allowed to see it."),

        FailureCategory.EwsError => new(
            CheckStatus.Success, CheckStatus.Success, CheckStatus.NotConfirmed,
            TroubleshootingArea.ExchangeService,
            "EWS returned an error response (see the EWS Response code). Look the code up in the Exchange Web Services documentation or ask the Exchange administrator; busy or throttling errors are usually transient."),

        FailureCategory.Cancelled => new(
            CheckStatus.NotEvaluated, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.None,
            "The probe was cancelled. Run it again to completion to get a result."),

        _ => new(
            CheckStatus.NotEvaluated, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated,
            TroubleshootingArea.Application,
            "The probe hit an error it did not anticipate. Run it again with Logging__LogLevel__Default=Debug and report the exception type shown above."),
    };
}
