using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ExchangeIntegration.Tests;

/// <summary>
/// Optional tests against a real Exchange server. They are skipped unless EXCHANGE_LIVE_TESTS is set, and they only
/// perform the same read-only Inbox GetFolder as the probe. Use a customer-approved test mailbox (see README).
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class LiveExchangeTests(ITestOutputHelper output)
{
    private static ExchangeConnectivityProbe CreateProbe(ExchangeOptions options, out HttpClient httpClient)
    {
        httpClient = ExchangeHttpClientFactory.Create(options);
        var ewsClient = new EwsClient(httpClient, Options.Create(options), NullLogger<EwsClient>.Instance);
        return new ExchangeConnectivityProbe(
            ewsClient,
            new WindowsProcessIdentityProvider(),
            Options.Create(options),
            NullLogger<ExchangeConnectivityProbe>.Instance);
    }

    [LiveExchangeFact]
    public async Task The_approved_test_mailbox_inbox_is_readable_with_the_process_identity()
    {
        var options = LiveExchangeSettings.LoadOptions();
        var failures = ExchangeOptionsValidator.GetFailures(options);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        var probe = CreateProbe(options, out var httpClient);
        using (httpClient)
        {
            var result = await probe.RunAsync();

            output.WriteLine(ConnectivityReport.Render(result));
            Assert.True(result.IsSuccess, $"{result.Category}: {result.Detail}");
            Assert.Equal("NoError", result.EwsResponseCode);
        }
    }

    [LiveExchangeFact(RequiresUnauthorizedMailbox = true)]
    public async Task A_mailbox_approved_for_security_testing_and_not_delegated_is_denied()
    {
        var options = LiveExchangeSettings.LoadOptions();
        options.Mailbox = LiveExchangeSettings.UnauthorizedMailbox!;
        var failures = ExchangeOptionsValidator.GetFailures(options);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        var probe = CreateProbe(options, out var httpClient);
        using (httpClient)
        {
            var result = await probe.RunAsync();

            output.WriteLine(ConnectivityReport.Render(result));

            // Authentication must still succeed; only the mailbox authorization may fail.
            Assert.Equal(FailureCategory.MailboxAuthorization, result.Category);
            Assert.Equal(CheckStatus.Success, result.WindowsAuthentication);
            Assert.Equal(CheckStatus.Failed, result.MailboxAccess);
        }
    }
}
