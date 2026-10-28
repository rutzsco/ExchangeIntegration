using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class EwsClientTests
{
    private const string Endpoint = "https://mail.contoso.com/EWS/Exchange.asmx";
    private const string Mailbox = "ap-invoices@contoso.com";

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _httpClient;

        public Harness(StubHttpMessageHandler handler, Action<ExchangeOptions>? configure = null, TimeSpan? timeout = null)
        {
            Handler = handler;
            var options = new ExchangeOptions { EwsUrl = Endpoint, Mailbox = Mailbox };
            configure?.Invoke(options);

            _httpClient = timeout is null
                ? ExchangeHttpClientFactory.Create(options, handler)
                : new HttpClient(handler) { Timeout = timeout.Value };
            Log = new CapturingLogger<EwsClient>();
            Client = new EwsClient(_httpClient, Options.Create(options), Log);
        }

        public StubHttpMessageHandler Handler { get; }

        public CapturingLogger<EwsClient> Log { get; }

        public EwsClient Client { get; }

        public Task<EwsGetFolderResult> GetInboxAsync(CancellationToken cancellationToken = default) =>
            Client.GetInboxFolderAsync(Mailbox, cancellationToken);

        public void Dispose() => _httpClient.Dispose();
    }

    private static StubHttpMessageHandler WaitsForever() => new(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK);
    });

    // --- Success -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Successful_get_folder_is_validated_by_the_ews_response_code()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()));

        var result = await harness.GetInboxAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(FailureCategory.None, result.Category);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("Success", result.EwsResponseClass);
        Assert.Equal("NoError", result.EwsResponseCode);
        Assert.Equal(SoapSamples.FolderId, result.FolderId);
        Assert.Equal("Inbox", result.FolderDisplayName);
        Assert.Equal("15.2.2562.17", result.ServerVersion);
    }

    [Fact]
    public async Task Request_is_one_post_of_a_read_only_get_folder_without_any_credentials_in_the_message()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()));

        await harness.GetInboxAsync();

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri(Endpoint), request.Uri);
        Assert.StartsWith("text/xml", request.ContentType, StringComparison.Ordinal);
        Assert.Contains("DistinguishedFolderId", request.Body);
        Assert.Contains(Mailbox, request.Body);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Equal(ExchangeHttpClientFactory.UserAgent, request.Headers["User-Agent"]);
    }

    [Fact]
    public async Task Only_noerror_counts_as_success_even_when_the_response_class_says_success()
    {
        var body = SoapSamples.Success().Replace("NoError", "ErrorItemNotFound");
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body));

        var result = await harness.GetInboxAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.EwsError, result.Category);
        Assert.Equal("ErrorItemNotFound", result.EwsResponseCode);
    }

    [Fact]
    public async Task Noerror_without_a_folder_identifier_is_not_accepted_as_success()
    {
        var body = SoapSamples.Success().Replace($"<t:FolderId Id=\"{SoapSamples.FolderId}\" ChangeKey=\"{SoapSamples.ChangeKey}\" />", string.Empty);
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body));

        var result = await harness.GetInboxAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.SoapProtocol, result.Category);
    }

    // --- HTTP-level failures -----------------------------------------------------------------------------

    [Fact]
    public async Task Http_401_is_an_authentication_failure_that_names_the_offered_schemes_but_never_a_token()
    {
        const string leakedToken = "TlRMTVNTUAACAAAAEXAMPLETOKENEXAMPLETOKENEXAMPLETOKENEXAMPLETOKEN";
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, string.Empty, configure: response =>
        {
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Negotiate");
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", $"NTLM {leakedToken}");
        });
        using var harness = new Harness(handler);

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Authentication, result.Category);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(new[] { "Negotiate", "NTLM" }, result.OfferedAuthenticationSchemes.ToArray());
        Assert.Contains("Negotiate, NTLM", result.Message);
        Assert.DoesNotContain(leakedToken, result.Message);
        Assert.DoesNotContain(leakedToken, harness.Log.Everything());
    }

    [Fact]
    public async Task Http_403_is_http_access_denied_and_not_an_authentication_failure()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.Forbidden));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.HttpAccessDenied, result.Category);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Error_statuses_that_are_not_ews_responses_are_unexpected_http_statuses(HttpStatusCode status)
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(status, "<html><body>Server error</body></html>", "text/html"));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.UnexpectedHttpStatus, result.Category);
        Assert.Equal(status, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task Redirects_are_reported_and_never_followed(HttpStatusCode status)
    {
        var handler = StubHttpMessageHandler.Returning(status, configure: response =>
            response.Headers.Location = new Uri("https://sso.contoso.com/login"));
        using var harness = new Harness(handler);

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.UnexpectedRedirect, result.Category);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task Redirect_reports_the_target_without_its_query_string()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Found, configure: response =>
            response.Headers.Location = new Uri("https://sso.contoso.com/login?token=SECRETTOKEN123"));
        using var harness = new Harness(handler);

        var result = await harness.GetInboxAsync();

        Assert.Contains("https://sso.contoso.com/login", result.Message);
        Assert.DoesNotContain("SECRETTOKEN123", result.Message);
        Assert.DoesNotContain("SECRETTOKEN123", harness.Log.Everything());
    }

    [Fact]
    public async Task Relative_redirect_reports_the_path_without_its_query_string()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.Found, configure: response =>
            response.Headers.Location = new Uri("/owa/auth/logon.aspx?url=SECRETTOKEN123", UriKind.Relative));
        using var harness = new Harness(handler);

        var result = await harness.GetInboxAsync();

        Assert.Contains("/owa/auth/logon.aspx", result.Message);
        Assert.DoesNotContain("SECRETTOKEN123", result.Message);
    }

    // --- SOAP-level failures -----------------------------------------------------------------------------

    [Fact]
    public async Task Ews_access_denied_inside_an_http_200_is_a_mailbox_authorization_failure()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.AccessDenied()));

        var result = await harness.GetInboxAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.MailboxAuthorization, result.Category);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("Error", result.EwsResponseClass);
        Assert.Equal("ErrorAccessDenied", result.EwsResponseCode);
        Assert.Contains("Access is denied", result.Message);
        Assert.Null(result.FolderId);
    }

    [Theory]
    [InlineData("ErrorNonExistentMailbox", "The SMTP address has no mailbox associated with it.")]
    [InlineData("ErrorInvalidSmtpAddress", "The SMTP address format is invalid.")]
    [InlineData("ErrorNonPrimarySmtpAddress", "The specified SMTP address is not the primary address.")]
    public async Task A_mailbox_that_cannot_be_resolved_is_reported_as_a_mailbox_resolution_failure(string code, string message)
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Error(code, message)));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.MailboxResolution, result.Category);
        Assert.Equal(code, result.EwsResponseCode);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public async Task Other_ews_error_codes_keep_their_specific_code()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            SoapSamples.Error("ErrorServerBusy", "The server cannot service this request right now.")));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.EwsError, result.Category);
        Assert.Equal("ErrorServerBusy", result.EwsResponseCode);
    }

    [Fact]
    public async Task Soap_fault_with_http_500_keeps_the_ews_error_code()
    {
        var body = SoapSamples.Fault("ErrorInvalidServerVersion", "The specified server version is invalid.");
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, body));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.EwsError, result.Category);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.Equal("ErrorInvalidServerVersion", result.EwsResponseCode);
    }

    [Fact]
    public async Task Soap_fault_denying_impersonation_is_a_mailbox_authorization_failure()
    {
        var body = SoapSamples.Fault("ErrorImpersonateUserDenied", "The account does not have permission to impersonate the requested user.");
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, body));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.MailboxAuthorization, result.Category);
    }

    [Theory]
    [InlineData("this is not xml")]
    [InlineData("")]
    [InlineData(SoapSamples.SignInPage)]
    [InlineData(SoapSamples.EnvelopeWithoutGetFolderResponse)]
    public async Task Invalid_soap_in_an_http_200_is_a_soap_protocol_failure(string body)
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body));

        var result = await harness.GetInboxAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.SoapProtocol, result.Category);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task A_malformed_response_code_is_a_soap_protocol_failure()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Error("No Error!", "x")));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.SoapProtocol, result.Category);
    }

    // --- Transport failures ------------------------------------------------------------------------------

    [Fact]
    public async Task Certificate_validation_failure_is_an_explicit_tls_failure()
    {
        var inner = new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot");
        var outer = new HttpRequestException("The SSL connection could not be established, see inner exception.", inner);
        using var harness = new Harness(StubHttpMessageHandler.Throwing(outer));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.TlsFailure, result.Category);
        Assert.Contains("UntrustedRoot", result.Message);
        Assert.Contains("never bypassed", result.Message);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task A_secure_connection_error_is_a_tls_failure_even_without_an_inner_exception()
    {
        var exception = new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.");
        using var harness = new Harness(StubHttpMessageHandler.Throwing(exception));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.TlsFailure, result.Category);
    }

    [Fact]
    public async Task Dns_failure_is_a_connectivity_failure()
    {
        var exception = new HttpRequestException(
            HttpRequestError.NameResolutionError,
            "No such host is known.",
            new SocketException((int)SocketError.HostNotFound));
        using var harness = new Harness(StubHttpMessageHandler.Throwing(exception));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Connectivity, result.Category);
        Assert.Contains("DNS name resolution failed", result.Message);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Connection_refused_is_a_connectivity_failure()
    {
        var exception = new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        using var harness = new Harness(StubHttpMessageHandler.Throwing(exception));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Connectivity, result.Category);
    }

    [Fact]
    public async Task A_tcp_connect_timeout_is_a_timeout_failure()
    {
        var exception = new HttpRequestException("Connection timed out", new SocketException((int)SocketError.TimedOut));
        using var harness = new Harness(StubHttpMessageHandler.Throwing(exception));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Timeout, result.Category);
    }

    [Fact]
    public async Task A_request_that_exceeds_the_http_timeout_is_a_timeout_failure()
    {
        using var harness = new Harness(WaitsForever(), timeout: TimeSpan.FromMilliseconds(150));

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Timeout, result.Category);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_a_timeout()
    {
        using var harness = new Harness(WaitsForever());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.GetInboxAsync(cancellation.Token));
    }

    // --- Refusals before anything is sent ----------------------------------------------------------------

    [Fact]
    public async Task A_plain_http_endpoint_is_refused_before_anything_is_sent()
    {
        using var harness = new Harness(
            StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()),
            options => options.EwsUrl = "http://mail.contoso.com/EWS/Exchange.asmx");

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Configuration, result.Category);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task An_unsupported_access_mode_is_refused_before_anything_is_sent()
    {
        using var harness = new Harness(
            StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()),
            options => options.AccessMode = ExchangeAccessMode.Impersonation);

        var result = await harness.GetInboxAsync();

        Assert.Equal(FailureCategory.Configuration, result.Category);
        Assert.Empty(harness.Handler.Requests);
    }

    // --- Logging and sanitizing --------------------------------------------------------------------------

    [Fact]
    public async Task Logs_contain_neither_bodies_nor_authentication_material_nor_the_full_folder_id()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()));

        await harness.GetInboxAsync();

        var logged = harness.Log.Everything();
        Assert.NotEmpty(harness.Log.Entries);
        Assert.DoesNotContain(SoapSamples.FolderId, logged);
        Assert.DoesNotContain(SoapSamples.ChangeKey, logged);
        Assert.DoesNotContain("GetFolderResponse", logged);
        Assert.DoesNotContain("Authorization", logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Redaction.Fingerprint(SoapSamples.FolderId), logged);
    }

    [Fact]
    public async Task Logs_for_a_failed_request_use_structured_properties_and_carry_no_raw_response()
    {
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.AccessDenied()));

        await harness.GetInboxAsync();

        var failure = Assert.Single(harness.Log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal("MailboxAuthorization", failure.Properties["FailureCategory"]);
        Assert.Equal("ErrorAccessDenied", failure.Properties["EwsResponseCode"]);
        Assert.Equal("200", failure.Properties["HttpStatus"]);
        Assert.DoesNotContain("GetFolderResponse", harness.Log.Everything());
    }

    [Fact]
    public async Task Server_supplied_error_text_is_sanitized_before_it_is_exposed()
    {
        var blob = new string('A', 60);
        var message = $"Access is denied.\r\nFake log line: {blob} {string.Join(' ', Enumerable.Repeat("word", 200))}";
        using var harness = new Harness(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Error("ErrorAccessDenied", message)));

        var result = await harness.GetInboxAsync();

        var text = Assert.IsType<string>(result.Message);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain(blob, text);
        Assert.Contains("[redacted]", text);
        Assert.True(text.Length <= Redaction.MaxMessageLength + 3, $"Message was {text.Length} characters.");
    }
}
