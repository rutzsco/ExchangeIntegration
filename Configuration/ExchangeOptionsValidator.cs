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
