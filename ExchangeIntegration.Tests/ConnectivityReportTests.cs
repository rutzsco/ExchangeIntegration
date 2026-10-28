using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;

namespace ExchangeIntegration.Tests;

public class ConnectivityReportTests
{
    private const string Identity = @"CONTOSO\svc-invoiceews";

    private static ConnectivityResult Success() => new()
    {
        WindowsIdentity = Identity,
        AccessMode = ExchangeAccessMode.Delegated,
        Endpoint = "https://mail.contoso.com/EWS/Exchange.asmx",
        Mailbox = "ap-invoices@contoso.com",
        Category = FailureCategory.None,
        HttpConnection = CheckStatus.Success,
        WindowsAuthentication = CheckStatus.Success,
        MailboxAccess = CheckStatus.Success,
        HttpStatusCode = 200,
        EwsResponseCode = "NoError",
        FolderIdFingerprint = Redaction.Fingerprint(SoapSamples.FolderId),
        ServerVersion = "15.2.2562.17",
    };

    private static ConnectivityResult AuthenticationFailure() => new()
    {
        WindowsIdentity = Identity,
        AccessMode = ExchangeAccessMode.Delegated,
        Endpoint = "https://mail.contoso.com/EWS/Exchange.asmx",
        Mailbox = "ap-invoices@contoso.com",
        Category = FailureCategory.Authentication,
        HttpConnection = CheckStatus.Success,
        WindowsAuthentication = CheckStatus.Failed,
        MailboxAccess = CheckStatus.NotEvaluated,
        HttpStatusCode = 401,
        Detail = "HTTP 401: Exchange did not accept the Windows credentials of this process. The server offered: Negotiate, NTLM.",
        LikelyArea = TroubleshootingArea.WindowsIdentity | TroubleshootingArea.ExchangeAuthentication,
        NextStep = "Check the identity.",
    };

    private static string[] Lines(string report) => report.Split(["\r\n", "\n"], StringSplitOptions.None);

    [Fact]
    public void Success_report_follows_the_documented_layout()
    {
        var lines = Lines(ConnectivityReport.Render(Success()));

        string[] expected =
        [
            "Exchange Integration - Connectivity Probe",
            "Runtime:",
            @"  Windows Identity: CONTOSO\svc-invoiceews",
            "  Access Mode: Delegated",
            "Exchange:",
            "  Endpoint: https://mail.contoso.com/EWS/Exchange.asmx",
            "  Target Mailbox: ap-invoices@contoso.com",
            "Results:",
            "  HTTP Connection: SUCCESS",
            "  Windows Authentication: SUCCESS",
            "  Mailbox Access: SUCCESS",
            "  EWS Operation: GetFolder",
            "  EWS Response: NoError",
            "Overall: SUCCESS",
        ];

        Assert.All(expected, line => Assert.Contains(line, lines));
    }

    [Fact]
    public void Success_report_does_not_claim_kerberos_was_used()
    {
        var report = ConnectivityReport.Render(Success());

        Assert.Contains("Negotiated Protocol: NOT VERIFIED", report);
        Assert.Contains("does not", report);
        Assert.Contains("Kerberos", report);
        Assert.DoesNotContain("Kerberos: SUCCESS", report);
        Assert.DoesNotContain("Negotiated Protocol: Kerberos", report);
    }

    [Fact]
    public void Success_report_prints_a_fingerprint_and_never_the_folder_identifier()
    {
        var report = ConnectivityReport.Render(Success());

        Assert.Contains($"fingerprint {Redaction.Fingerprint(SoapSamples.FolderId)}", report);
        Assert.DoesNotContain(SoapSamples.FolderId, report);
    }

    [Fact]
    public void Failure_report_names_the_category_the_area_and_the_next_step()
    {
        var lines = Lines(ConnectivityReport.Render(AuthenticationFailure()));

        Assert.Contains("  Windows Authentication: FAILED", lines);
        Assert.Contains("  Mailbox Access: NOT EVALUATED", lines);
        Assert.Contains("  HTTP Status: 401", lines);
        Assert.Contains("  Category: Authentication", lines);
        Assert.Contains("  Likely Area: Windows identity / Exchange authentication", lines);
        Assert.Contains("  Next Step: Check the identity.", lines);
        Assert.Contains("Overall: FAILURE (exit code 5)", lines);
        Assert.DoesNotContain("Overall: SUCCESS", lines);
    }

    [Fact]
    public void Failure_report_has_no_protocol_line_because_authentication_was_not_established()
    {
        Assert.DoesNotContain("Negotiated Protocol", ConnectivityReport.Render(AuthenticationFailure()));
    }

    [Fact]
    public void Configuration_failure_report_lists_the_errors_and_has_no_results_section()
    {
        var result = ConnectivityResult.ForConfigurationFailure(Identity, ["Exchange:EwsUrl is required.", "Exchange:Mailbox is required."]);

        var report = ConnectivityReport.Render(result);
        var lines = Lines(report);

        Assert.Contains("  - Exchange:EwsUrl is required.", lines);
        Assert.Contains("  - Exchange:Mailbox is required.", lines);
        Assert.Contains("  Category: Configuration", lines);
        Assert.Contains("Overall: FAILURE (exit code 2)", lines);
        Assert.DoesNotContain("Results:", report);
        Assert.DoesNotContain("Exchange:" + Environment.NewLine, report);
    }

    [Fact]
    public void Configuration_failure_messages_are_sanitized()
    {
        var result = ConnectivityResult.ForConfigurationFailure(Identity, ["line one\r\nforged: Overall: SUCCESS"]);

        var report = ConnectivityReport.Render(result);

        Assert.DoesNotContain("\nforged", report);
        Assert.DoesNotContain("Overall: SUCCESS", Lines(report));
        Assert.Contains("Overall: FAILURE (exit code 2)", Lines(report));
    }
}
