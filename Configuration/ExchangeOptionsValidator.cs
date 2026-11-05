using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

/// <summary>Validates <see cref="ExchangeOptions"/>; every problem is reported at once so an operator can fix them in one pass.</summary>
public sealed class ExchangeOptionsValidator : IValidateOptions<ExchangeOptions>
{
    private const int MaxMailboxLength = 254;

    public ValidateOptionsResult Validate(string? name, ExchangeOptions options)
    {
        var failures = GetFailures(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> GetFailures(ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateEwsUrl(options.EwsUrl, failures);
        ValidateMailbox(options.Mailbox, failures);
        ValidateAccessMode(options.AccessMode, failures);

        if (options.TimeoutSeconds < 1 || options.TimeoutSeconds > ExchangeOptions.MaxTimeoutSeconds)
        {
            failures.Add($"Exchange:TimeoutSeconds must be between 1 and {ExchangeOptions.MaxTimeoutSeconds} seconds.");
        }

        return failures;
    }

    /// <summary>The mailbox address check alone; local mode (--local) has no EWS endpoint or Windows authentication.</summary>
    public static IReadOnlyList<string> GetMailboxFailures(ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateMailbox(options.Mailbox, failures);
        return failures;
    }

    /// <summary>The settings only service mode (--run) uses, so they never stop the read-only probe.</summary>
    public static IReadOnlyList<string> GetServiceModeFailures(ExchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.PollIntervalSeconds < 5 || options.PollIntervalSeconds > 3600)
        {
            failures.Add("Exchange:PollIntervalSeconds must be between 5 and 3600 seconds.");
        }

        if (options.MaxMessagesPerPoll < 1 || options.MaxMessagesPerPoll > 500)
        {
            failures.Add("Exchange:MaxMessagesPerPoll must be between 1 and 500.");
        }

        if ((options.MoveFolders ?? []).Any(folder => string.IsNullOrWhiteSpace(folder) || folder.Length > 255 || folder.Contains('/') || folder.Contains('\\')))
        {
            failures.Add("Exchange:MoveFolders entries must be non-empty folder display names (top-level folders only, no paths).");
        }

        return failures;
    }

    private static void ValidateEwsUrl(string? ewsUrl, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(ewsUrl))
        {
            failures.Add("Exchange:EwsUrl is required, for example https://mail.contoso.com/EWS/Exchange.asmx.");
            return;
        }

        if (!Uri.TryCreate(ewsUrl, UriKind.Absolute, out var uri))
        {
            failures.Add("Exchange:EwsUrl is not a valid absolute URL; include the https:// scheme.");
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Exchange:EwsUrl must use HTTPS (found '{uri.Scheme}'); Windows authentication must not run over an unencrypted channel.");
        }

        // The URL itself is never echoed here because user info would expose the embedded secret.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failures.Add("Exchange:EwsUrl must not contain embedded credentials; the Windows identity of the process is used.");
        }
    }

    private static void ValidateMailbox(string? mailbox, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            failures.Add("Exchange:Mailbox is required: the primary SMTP address of the target mailbox.");
            return;
        }

        var isPlainAddress = mailbox.Length <= MaxMailboxLength
            && MailAddress.TryCreate(mailbox, out var parsed)
            && string.Equals(parsed.Address, mailbox, StringComparison.OrdinalIgnoreCase);

        if (!isPlainAddress)
        {
            failures.Add("Exchange:Mailbox must be a plain SMTP address such as ap-invoices@contoso.com (no display name or surrounding spaces).");
        }
    }

    private static void ValidateAccessMode(ExchangeAccessMode accessMode, List<string> failures)
    {
        if (!Enum.IsDefined(accessMode))
        {
            failures.Add($"Exchange:AccessMode value '{(int)accessMode}' is not recognized. The supported value is Delegated.");
        }
        else if (accessMode != ExchangeAccessMode.Delegated)
        {
            failures.Add($"Exchange:AccessMode '{accessMode}' is not supported in this milestone. The supported value is Delegated.");
        }
    }
}
