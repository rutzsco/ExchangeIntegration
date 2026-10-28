using System.Net;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class ExchangeConnectivityProbeTests
{
    private const string Identity = @"CONTOSO\svc-invoiceews";

    private sealed class FakeIdentityProvider(string name) : IProcessIdentityProvider
    {
        public string GetCurrentIdentityName() => name;
    }

    private sealed class FakeEwsClient(Func<string, CancellationToken, Task<EwsGetFolderResult>> handler) : IEwsClient
    {
        public string? RequestedMailbox { get; private set; }

        public Task<EwsGetFolderResult> GetInboxFolderAsync(string mailbox, CancellationToken cancellationToken = default)
        {
            RequestedMailbox = mailbox;
            return handler(mailbox, cancellationToken);
        }
    }

    private static ExchangeOptions ValidOptions() => new()
    {
        EwsUrl = "https://mail.contoso.com/EWS/Exchange.asmx",
        Mailbox = "ap-invoices@contoso.com",
    };

    private static EwsGetFolderResult EwsResult(
        FailureCategory category,
        HttpStatusCode? status = null,
        string? code = null,
        string? folderId = null) => new()
        {
            Category = category,
            StatusCode = status,
            EwsResponseCode = code,
            FolderId = folderId,
            Message = "detail from the client",
        };

    private static (ExchangeConnectivityProbe Probe, FakeEwsClient Client, CapturingLogger<ExchangeConnectivityProbe> Log) CreateProbe(
        Func<string, CancellationToken, Task<EwsGetFolderResult>> handler)
    {
        var client = new FakeEwsClient(handler);
        var log = new CapturingLogger<ExchangeConnectivityProbe>();
        var probe = new ExchangeConnectivityProbe(client, new FakeIdentityProvider(Identity), Options.Create(ValidOptions()), log);
        return (probe, client, log);
    }

    private static Task<ConnectivityResult> Run(EwsGetFolderResult ewsResult) =>
        CreateProbe((_, _) => Task.FromResult(ewsResult)).Probe.RunAsync();

    [Fact]
    public async Task Success_is_reported_only_for_a_successful_get_folder()
    {
        var (probe, client, _) = CreateProbe((_, _) => Task.FromResult(
            EwsResult(FailureCategory.None, HttpStatusCode.OK, "NoError", SoapSamples.FolderId)));

        var result = await probe.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CheckStatus.Success, result.HttpConnection);
        Assert.Equal(CheckStatus.Success, result.WindowsAuthentication);
        Assert.Equal(CheckStatus.Success, result.MailboxAccess);
        Assert.Equal(Identity, result.WindowsIdentity);
        Assert.Equal(ExchangeAccessMode.Delegated, result.AccessMode);
        Assert.Equal("https://mail.contoso.com/EWS/Exchange.asmx", result.Endpoint);
        Assert.Equal("ap-invoices@contoso.com", result.Mailbox);
        Assert.Equal("ap-invoices@contoso.com", client.RequestedMailbox);
        Assert.Equal("GetFolder", result.EwsOperation);
        Assert.Equal("NoError", result.EwsResponseCode);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task The_folder_identifier_is_carried_only_as_a_fingerprint()
    {
        var result = await Run(EwsResult(FailureCategory.None, HttpStatusCode.OK, "NoError", SoapSamples.FolderId));

        Assert.Equal(Redaction.Fingerprint(SoapSamples.FolderId), result.FolderIdFingerprint);
        Assert.NotEqual(SoapSamples.FolderId, result.FolderIdFingerprint);
        Assert.Equal(8, result.FolderIdFingerprint!.Length);
    }

    [Theory]
    [InlineData(FailureCategory.Connectivity, CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.TlsFailure, CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.Timeout, CheckStatus.Failed, CheckStatus.NotEvaluated, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.Authentication, CheckStatus.Success, CheckStatus.Failed, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.HttpAccessDenied, CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.UnexpectedRedirect, CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.UnexpectedHttpStatus, CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.SoapProtocol, CheckStatus.Success, CheckStatus.NotConfirmed, CheckStatus.NotEvaluated)]
    [InlineData(FailureCategory.MailboxAuthorization, CheckStatus.Success, CheckStatus.Success, CheckStatus.Failed)]
    [InlineData(FailureCategory.MailboxResolution, CheckStatus.Success, CheckStatus.Success, CheckStatus.Failed)]
    [InlineData(FailureCategory.EwsError, CheckStatus.Success, CheckStatus.Success, CheckStatus.NotConfirmed)]
    public async Task Each_failure_category_reports_exactly_what_was_established(
        FailureCategory category,
        CheckStatus http,
        CheckStatus authentication,
        CheckStatus mailbox)
    {
        var result = await Run(EwsResult(category));

        Assert.False(result.IsSuccess);
        Assert.Equal(category, result.Category);
        Assert.Equal((int)category, result.ExitCode);
        Assert.Equal(http, result.HttpConnection);
        Assert.Equal(authentication, result.WindowsAuthentication);
        Assert.Equal(mailbox, result.MailboxAccess);
        Assert.False(string.IsNullOrWhiteSpace(result.NextStep));
        Assert.NotEqual(TroubleshootingArea.None, result.LikelyArea);
        Assert.Equal("detail from the client", result.Detail);
    }

    [Theory]
    [InlineData(FailureCategory.Authentication, TroubleshootingArea.WindowsIdentity)]
    [InlineData(FailureCategory.Authentication, TroubleshootingArea.ExchangeAuthentication)]
    [InlineData(FailureCategory.HttpAccessDenied, TroubleshootingArea.ExchangeAuthentication)]
    [InlineData(FailureCategory.MailboxAuthorization, TroubleshootingArea.MailboxPermissions)]
    [InlineData(FailureCategory.MailboxResolution, TroubleshootingArea.MailboxPermissions)]
    [InlineData(FailureCategory.SoapProtocol, TroubleshootingArea.SoapImplementation)]
    [InlineData(FailureCategory.Connectivity, TroubleshootingArea.Network)]
    [InlineData(FailureCategory.TlsFailure, TroubleshootingArea.Network)]
    public async Task Failures_point_at_the_area_where_the_next_step_belongs(FailureCategory category, TroubleshootingArea expectedArea)
    {
        var result = await Run(EwsResult(category));

        Assert.True(result.LikelyArea.HasFlag(expectedArea), $"{category} should point at {expectedArea} but pointed at {result.LikelyArea}.");
    }

    [Fact]
    public async Task Authentication_and_mailbox_authorization_failures_are_distinguishable()
    {
        var authentication = await Run(EwsResult(FailureCategory.Authentication, HttpStatusCode.Unauthorized));
        var authorization = await Run(EwsResult(FailureCategory.MailboxAuthorization, HttpStatusCode.OK, "ErrorAccessDenied"));

        Assert.Equal(CheckStatus.Failed, authentication.WindowsAuthentication);
        Assert.Equal(CheckStatus.Success, authorization.WindowsAuthentication);
        Assert.Equal(CheckStatus.Failed, authorization.MailboxAccess);
        Assert.NotEqual(authentication.ExitCode, authorization.ExitCode);
        Assert.NotEqual(authentication.Category, authorization.Category);
    }

    [Theory]
    [InlineData(FailureCategory.Authentication)]
    [InlineData(FailureCategory.HttpAccessDenied)]
    [InlineData(FailureCategory.UnexpectedRedirect)]
    [InlineData(FailureCategory.UnexpectedHttpStatus)]
    [InlineData(FailureCategory.SoapProtocol)]
    public async Task Reaching_the_endpoint_is_never_taken_as_proof_of_authentication(FailureCategory category)
    {
        var result = await Run(EwsResult(category));

        Assert.Equal(CheckStatus.Success, result.HttpConnection);
        Assert.NotEqual(CheckStatus.Success, result.WindowsAuthentication);
    }

    [Fact]
    public async Task Unexpected_exceptions_become_a_result_instead_of_crashing_the_probe()
    {
        var (probe, _, log) = CreateProbe((_, _) => throw new InvalidOperationException("boom"));

        var result = await probe.RunAsync();

        Assert.Equal(FailureCategory.Unexpected, result.Category);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("InvalidOperationException", result.Detail);
        Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Cancellation_is_reported_as_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var (probe, _, _) = CreateProbe((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(EwsResult(FailureCategory.None));
        });

        var result = await probe.RunAsync(cancellation.Token);

        Assert.Equal(FailureCategory.Cancelled, result.Category);
        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public void Every_failure_category_has_a_distinct_nonzero_exit_code()
    {
        var codes = Enum.GetValues<FailureCategory>()
            .Where(category => category != FailureCategory.None)
            .Select(category => (int)category)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.InRange(code, 1, 255));
        Assert.Equal(0, (int)FailureCategory.None);
    }

    [Fact]
    public async Task The_windows_identity_is_logged_as_a_structured_property_without_the_folder_identifier()
    {
        var (probe, _, log) = CreateProbe((_, _) => Task.FromResult(
            EwsResult(FailureCategory.None, HttpStatusCode.OK, "NoError", SoapSamples.FolderId)));

        await probe.RunAsync();

        Assert.Contains(log.Entries, entry => entry.Properties.TryGetValue("WindowsIdentity", out var value) && value == Identity);
        Assert.DoesNotContain(SoapSamples.FolderId, log.Everything());
    }

    // --- The probe over the real EWS client and a mocked HTTP transport ----------------------------------

    private static ExchangeConnectivityProbe CreateEndToEndProbe(StubHttpMessageHandler handler)
    {
        var options = ValidOptions();
        var httpClient = ExchangeHttpClientFactory.Create(options, handler);
        var ewsClient = new EwsClient(httpClient, Options.Create(options), new CapturingLogger<EwsClient>());
        return new ExchangeConnectivityProbe(ewsClient, new FakeIdentityProvider(Identity), Options.Create(options), new CapturingLogger<ExchangeConnectivityProbe>());
    }

    [Fact]
    public async Task End_to_end_a_good_response_validates_authentication_and_mailbox_access()
    {
        var probe = CreateEndToEndProbe(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.Success()));

        var result = await probe.RunAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("NoError", result.EwsResponseCode);
        Assert.Equal("15.2.2562.17", result.ServerVersion);
        Assert.Equal(Redaction.Fingerprint(SoapSamples.FolderId), result.FolderIdFingerprint);
    }

    [Fact]
    public async Task End_to_end_http_401_is_an_authentication_failure_with_the_mailbox_untested()
    {
        var probe = CreateEndToEndProbe(StubHttpMessageHandler.Returning(HttpStatusCode.Unauthorized));

        var result = await probe.RunAsync();

        Assert.Equal(FailureCategory.Authentication, result.Category);
        Assert.Equal(CheckStatus.Failed, result.WindowsAuthentication);
        Assert.Equal(CheckStatus.NotEvaluated, result.MailboxAccess);
        Assert.Equal(401, result.HttpStatusCode);
    }

    [Fact]
    public async Task End_to_end_ews_access_denied_is_a_mailbox_authorization_failure_after_successful_authentication()
    {
        var probe = CreateEndToEndProbe(StubHttpMessageHandler.Returning(HttpStatusCode.OK, SoapSamples.AccessDenied()));

        var result = await probe.RunAsync();

        Assert.Equal(FailureCategory.MailboxAuthorization, result.Category);
        Assert.Equal(CheckStatus.Success, result.WindowsAuthentication);
        Assert.Equal(CheckStatus.Failed, result.MailboxAccess);
        Assert.Equal("ErrorAccessDenied", result.EwsResponseCode);
    }
}
