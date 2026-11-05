using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

/// <summary>Validates <see cref="AzureOptions"/>. Registered only in service mode, so the probe never needs Azure settings.</summary>
/// <param name="localMode">Local mode (--local) uses no Azure endpoints, only the container and queue names (as folder names).</param>
public sealed class AzureOptionsValidator(bool localMode = false) : IValidateOptions<AzureOptions>
{
    // Container and queue names: lowercase letters, digits and single hyphens (the stricter blob container rule).
    private static readonly Regex EntityName = new(
        "^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){2,62}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public ValidateOptionsResult Validate(string? name, AzureOptions options)
    {
        var failures = localMode ? GetNameFailures(options) : GetFailures(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> GetFailures(AzureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Uri.TryCreate(options.BlobServiceUri, UriKind.Absolute, out var blob)
            || !string.Equals(blob.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Azure:BlobServiceUri is required and must be an https:// URL, for example https://contosoinvoices.blob.core.windows.net.");
        }
        else if (!string.IsNullOrEmpty(blob.Query) || !string.IsNullOrEmpty(blob.UserInfo))
        {
            // A SAS token or embedded credentials would be a stored secret; the adapter uses Entra ID only.
            failures.Add("Azure:BlobServiceUri must not contain a query string (SAS token) or credentials; access uses Entra ID.");
        }

        var serviceBus = options.ServiceBusNamespace?.Trim() ?? string.Empty;
        if (serviceBus.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase) || serviceBus.Contains("SharedAccessKey", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Azure:ServiceBusNamespace must be a namespace host name, not a connection string; access uses Entra ID.");
        }
        else if (serviceBus.Length == 0 || serviceBus.Contains("://", StringComparison.Ordinal) || Uri.CheckHostName(serviceBus) != UriHostNameType.Dns || !serviceBus.Contains('.'))
        {
            failures.Add("Azure:ServiceBusNamespace is required: the fully qualified host name, for example contoso-invoices.servicebus.windows.net.");
        }

        failures.AddRange(GetNameFailures(options));
        return failures;
    }

    /// <summary>The container and queue name checks alone.</summary>
    public static IReadOnlyList<string> GetNameFailures(AzureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        CheckName("Azure:IntakeContainer", options.IntakeContainer, failures);
        CheckName("Azure:InvoiceReceivedQueue", options.InvoiceReceivedQueue, failures);
        CheckName("Azure:CommandsQueue", options.CommandsQueue, failures);
        CheckName("Azure:CommandResultsQueue", options.CommandResultsQueue, failures);

        return failures;
    }

    private static void CheckName(string setting, string? value, List<string> failures)
    {
        if (value is null || !EntityName.IsMatch(value))
        {
            failures.Add($"{setting} must be 3-63 lowercase letters, digits or single hyphens.");
        }
    }
}
