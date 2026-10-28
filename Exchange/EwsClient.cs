using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Exchange;

/// <summary>
/// Sends the read-only GetFolder request over the supplied <see cref="HttpClient"/> and classifies what comes back.
/// It never logs request or response bodies, authentication headers or full folder identifiers.
/// </summary>
public sealed class EwsClient : IEwsClient
{
    public const string GetFolderOperation = "GetFolder";

    private readonly HttpClient _httpClient;
    private readonly ExchangeOptions _options;
    private readonly ILogger<EwsClient> _logger;

    public EwsClient(HttpClient httpClient, IOptions<ExchangeOptions> options, ILogger<EwsClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EwsGetFolderResult> GetInboxFolderAsync(string mailbox, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailbox);

        var result = await ExecuteAsync(mailbox, cancellationToken);
        LogResult(result);
        return result;
    }

    private async Task<EwsGetFolderResult> ExecuteAsync(string mailbox, CancellationToken cancellationToken)
    {
        // Defense in depth: configuration validation already requires HTTPS, but the client must never
        // negotiate Windows authentication over an unencrypted channel regardless of how it was constructed.
        if (!Uri.TryCreate(_options.EwsUrl, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(FailureCategory.Configuration, "Exchange:EwsUrl must be an absolute HTTPS URL. No request was sent.");
        }

        string requestXml;
        try
        {
            requestXml = EwsRequestBuilder.BuildGetInboxFolderRequest(mailbox, _options.AccessMode);
        }
        catch (NotSupportedException exception)
        {
            return Failure(FailureCategory.Configuration, $"{exception.Message} No request was sent.");
        }

        _logger.LogInformation(
            "Sending EWS {Operation} for the Inbox of {Mailbox} to {Endpoint} (access mode {AccessMode}).",
            GetFolderOperation,
            mailbox,
            _options.GetDisplayEndpoint(),
            _options.AccessMode);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestXml, Encoding.UTF8, "text/xml"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller did not cancel, so HttpClient.Timeout expired.
            return Failure(
                FailureCategory.Timeout,
                $"No complete response was received within {_httpClient.Timeout.TotalSeconds:0.##} seconds.");
        }
        catch (HttpRequestException exception)
        {
            return ClassifyTransportFailure(exception);
        }

        using (response)
        {
            return await InterpretResponseAsync(response, cancellationToken);
        }
    }

    private async Task<EwsGetFolderResult> InterpretResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var statusNumber = (int)status;

        if (status == HttpStatusCode.Unauthorized)
        {
            var schemes = GetOfferedSchemes(response);
            var offered = schemes.Count > 0 ? $" The server offered: {string.Join(", ", schemes)}." : string.Empty;
            return Failure(
                FailureCategory.Authentication,
                "HTTP 401: Exchange did not accept the Windows credentials of this process." + offered,
                status,
                schemes: schemes);
        }

        if (status == HttpStatusCode.Forbidden)
        {
            return Failure(
                FailureCategory.HttpAccessDenied,
                "HTTP 403: the request was refused before mailbox permissions were evaluated.",
                status);
        }

        if (statusNumber is >= 300 and < 400)
        {
            return Failure(
                FailureCategory.UnexpectedRedirect,
                $"HTTP {statusNumber}: the endpoint redirected to {DescribeRedirect(response)}. Redirects are not followed so credentials are never forwarded.",
                status);
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(
                FailureCategory.SoapProtocol,
                $"The HTTP {statusNumber} response body could not be decoded ({exception.GetType().Name}).",
                status);
        }

        EwsParsedResponse parsed;
        try
        {
            parsed = EwsResponseParser.Parse(body);
        }
        catch (EwsProtocolException exception)
        {
            return response.IsSuccessStatusCode
                ? Failure(
                    FailureCategory.SoapProtocol,
                    $"The HTTP {statusNumber} response is not a valid EWS SOAP response: {exception.Message}",
                    status)
                : Failure(
                    FailureCategory.UnexpectedHttpStatus,
                    $"HTTP {statusNumber} ({status}) with a body that is not an EWS SOAP fault.",
                    status);
        }

        return Classify(parsed, status);
    }

    private static EwsGetFolderResult Classify(EwsParsedResponse parsed, HttpStatusCode status)
    {
        if (parsed.Fault is { } fault)
        {
            var code = fault.ResponseCode;
            var detail = Redaction.SanitizeMessage(fault.Message ?? fault.FaultString);
            var codeText = code is null ? string.Empty : $" ({code})";
            return Failure(
                CategoryFor(code),
                $"EWS returned a SOAP fault{codeText}: {detail}",
                status,
                ewsResponseCode: code,
                serverVersion: parsed.ServerVersion);
        }

        if (parsed.GetFolder is not { } getFolder)
        {
            return Failure(
                FailureCategory.SoapProtocol,
                "The response contained neither a GetFolder response nor a SOAP fault.",
                status);
        }

        if (getFolder.ResponseClass == "Success" && getFolder.ResponseCode == "NoError")
        {
            if (getFolder.FolderId is null)
            {
                return Failure(
                    FailureCategory.SoapProtocol,
                    "GetFolder reported NoError but returned no folder identifier.",
                    status,
                    getFolder.ResponseClass,
                    getFolder.ResponseCode,
                    serverVersion: parsed.ServerVersion);
            }

            return new EwsGetFolderResult
            {
                Category = FailureCategory.None,
                StatusCode = status,
                EwsResponseClass = getFolder.ResponseClass,
                EwsResponseCode = getFolder.ResponseCode,
                Message = "GetFolder on the Inbox returned NoError.",
                FolderId = getFolder.FolderId,
                FolderDisplayName = getFolder.FolderDisplayName is null ? null : Redaction.SanitizeMessage(getFolder.FolderDisplayName),
                ServerVersion = parsed.ServerVersion,
            };
        }

        return Failure(
            CategoryFor(getFolder.ResponseCode),
            $"EWS {getFolder.ResponseClass} response {getFolder.ResponseCode}: {Redaction.SanitizeMessage(getFolder.MessageText)}",
            status,
            getFolder.ResponseClass,
            getFolder.ResponseCode,
            serverVersion: parsed.ServerVersion);
    }

    private static FailureCategory CategoryFor(string? ewsResponseCode) => ewsResponseCode switch
    {
        "ErrorAccessDenied" or "ErrorImpersonateUserDenied" or "ErrorImpersonationDenied" => FailureCategory.MailboxAuthorization,
        "ErrorNonExistentMailbox" or "ErrorInvalidSmtpAddress" or "ErrorMissingEmailAddress" or "ErrorNonPrimarySmtpAddress" => FailureCategory.MailboxResolution,
        _ => FailureCategory.EwsError,
    };

    private EwsGetFolderResult ClassifyTransportFailure(HttpRequestException exception)
    {
        _logger.LogDebug(exception, "EWS transport failure.");

        var detail = Describe(exception);

        if (IsTlsFailure(exception))
        {
            return Failure(
                FailureCategory.TlsFailure,
                $"TLS negotiation or server certificate validation failed ({detail}). Certificate validation is never bypassed.");
        }

        if (exception.HttpRequestError == HttpRequestError.UserAuthenticationError)
        {
            return Failure(FailureCategory.Authentication, $"The Windows credentials could not be used to authenticate ({detail}).");
        }

        if (IsSocketTimeout(exception))
        {
            return Failure(FailureCategory.Timeout, $"The connection to Exchange timed out ({detail}).");
        }

        var reason = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS name resolution failed",
            HttpRequestError.ConnectionError => "the TCP connection could not be established",
            HttpRequestError.ProxyTunnelError => "the proxy tunnel could not be established",
            _ => "the HTTP request failed",
        };

        return Failure(FailureCategory.Connectivity, $"The Exchange endpoint was not reached: {reason} ({detail}).");
    }

    private static bool IsTlsFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSocketTimeout(HttpRequestException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException || current is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(Exception exception)
    {
        var innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        return $"{innermost.GetType().Name}: {innermost.Message}";
    }

    // Only scheme names are collected, never the challenge or token parameters that follow them.
    private static IReadOnlyList<string> GetOfferedSchemes(HttpResponseMessage response) =>
        response.Headers.WwwAuthenticate
            .Select(header => Redaction.SanitizeMessage(header.Scheme))
            .Where(scheme => scheme.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

    // Only scheme, host and path are reported: the query string of a sign-in redirect can carry tokens.
    private static string DescribeRedirect(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return "an unspecified location";
        }

        return location.IsAbsoluteUri
            ? location.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : location.OriginalString.Split('?', '#')[0];
    }

    private static EwsGetFolderResult Failure(
        FailureCategory category,
        string message,
        HttpStatusCode? statusCode = null,
        string? ewsResponseClass = null,
        string? ewsResponseCode = null,
        IReadOnlyList<string>? schemes = null,
        string? serverVersion = null) => new()
        {
            Category = category,
            Message = Redaction.SanitizeMessage(message),
            StatusCode = statusCode,
            EwsResponseClass = ewsResponseClass,
            EwsResponseCode = ewsResponseCode,
            OfferedAuthenticationSchemes = schemes ?? [],
            ServerVersion = serverVersion,
        };

    private void LogResult(EwsGetFolderResult result)
    {
        if (result.Succeeded)
        {
            _logger.LogInformation(
                "EWS {Operation} succeeded: HttpStatus={HttpStatus} EwsResponseCode={EwsResponseCode} FolderIdFingerprint={FolderIdFingerprint} ServerVersion={ServerVersion}",
                GetFolderOperation,
                (int?)result.StatusCode,
                result.EwsResponseCode,
                Redaction.Fingerprint(result.FolderId),
                result.ServerVersion);
            return;
        }

        _logger.LogWarning(
            "EWS {Operation} failed: Category={FailureCategory} HttpStatus={HttpStatus} EwsResponseCode={EwsResponseCode} Detail={Detail}",
            GetFolderOperation,
            result.Category,
            (int?)result.StatusCode,
            result.EwsResponseCode,
            result.Message);
    }
}
