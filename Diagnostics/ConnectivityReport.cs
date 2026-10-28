using System.Text;
using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Diagnostics;

/// <summary>Renders a <see cref="ConnectivityResult"/> as the concise console report of the probe.</summary>
public static class ConnectivityReport
{
    private static readonly (TroubleshootingArea Area, string Label)[] AreaLabels =
    [
        (TroubleshootingArea.Network, "Network and TLS"),
        (TroubleshootingArea.WindowsIdentity, "Windows identity"),
        (TroubleshootingArea.ExchangeAuthentication, "Exchange authentication"),
        (TroubleshootingArea.MailboxPermissions, "Mailbox permissions"),
        (TroubleshootingArea.SoapImplementation, "SOAP implementation"),
        (TroubleshootingArea.ExchangeService, "Exchange service"),
        (TroubleshootingArea.Configuration, "Configuration"),
        (TroubleshootingArea.Application, "This application"),
    ];

    public static string Render(ConnectivityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var report = new StringBuilder();
        report.AppendLine("Exchange Integration - Connectivity Probe");
        report.AppendLine();
        report.AppendLine("Runtime:");
        report.AppendLine($"  Windows Identity: {result.WindowsIdentity}");
        if (result.AccessMode is { } accessMode)
        {
            report.AppendLine($"  Access Mode: {accessMode}");
        }

        if (result.Endpoint is not null)
        {
            report.AppendLine();
            report.AppendLine("Exchange:");
            report.AppendLine($"  Endpoint: {result.Endpoint}");
            report.AppendLine($"  Target Mailbox: {result.Mailbox}");
        }

        report.AppendLine();
        if (result.ConfigurationErrors.Count > 0)
        {
            report.AppendLine("Configuration errors (nothing was sent to Exchange):");
            foreach (var error in result.ConfigurationErrors)
            {
                report.AppendLine($"  - {error}");
            }
        }
        else
        {
            AppendResults(report, result);
        }

        if (!result.IsSuccess)
        {
            report.AppendLine();
            report.AppendLine("Failure:");
            report.AppendLine($"  Category: {result.Category}");
            if (!string.IsNullOrEmpty(result.Detail))
            {
                report.AppendLine($"  Detail: {result.Detail}");
            }

            if (result.LikelyArea != TroubleshootingArea.None)
            {
                report.AppendLine($"  Likely Area: {DescribeAreas(result.LikelyArea)}");
            }

            if (!string.IsNullOrEmpty(result.NextStep))
            {
                report.AppendLine($"  Next Step: {result.NextStep}");
            }
        }

        report.AppendLine();
        report.AppendLine(result.IsSuccess ? "Overall: SUCCESS" : $"Overall: FAILURE (exit code {result.ExitCode})");

        if (result.IsSuccess)
        {
            report.AppendLine();
            report.AppendLine("Note: this shows that the Windows identity above authenticated and could open the Inbox. It does not");
            report.AppendLine("prove that Kerberos was used (NTLM may have been negotiated) or that the identity of the eventual");
            report.AppendLine("Windows service will succeed. See the README for how to verify both.");
        }

        return report.ToString();
    }

    private static void AppendResults(StringBuilder report, ConnectivityResult result)
    {
        report.AppendLine("Results:");
        report.AppendLine($"  HTTP Connection: {Format(result.HttpConnection)}");
        report.AppendLine($"  Windows Authentication: {Format(result.WindowsAuthentication)}");
        report.AppendLine($"  Mailbox Access: {Format(result.MailboxAccess)}");
        report.AppendLine($"  EWS Operation: {result.EwsOperation}");
        report.AppendLine($"  EWS Response: {result.EwsResponseCode ?? "(none)"}");

        if (result.HttpStatusCode is { } statusCode)
        {
            report.AppendLine($"  HTTP Status: {statusCode}");
        }

        if (result.ServerVersion is not null)
        {
            report.AppendLine($"  Exchange Server Version: {result.ServerVersion}");
        }

        if (result.FolderIdFingerprint is not null)
        {
            report.AppendLine($"  Inbox Folder Id: returned (fingerprint {result.FolderIdFingerprint}; the identifier is not printed)");
        }

        if (result.WindowsAuthentication == CheckStatus.Success)
        {
            report.AppendLine("  Negotiated Protocol: NOT VERIFIED (Kerberos versus NTLM cannot be told from this response; see README)");
        }
    }

    private static string Format(CheckStatus status) => status switch
    {
        CheckStatus.Success => "SUCCESS",
        CheckStatus.Failed => "FAILED",
        CheckStatus.NotConfirmed => "NOT CONFIRMED",
        _ => "NOT EVALUATED",
    };

    private static string DescribeAreas(TroubleshootingArea areas) =>
        string.Join(" / ", AreaLabels.Where(entry => areas.HasFlag(entry.Area)).Select(entry => entry.Label));
}
