namespace ExchangeIntegration.Configuration;

/// <summary>
/// Azure settings bound from the "Azure" configuration section. Only service mode (--run) uses them. There are no
/// keys or connection strings: the adapter signs in with DefaultAzureCredential (managed identity through Azure Arc,
/// a certificate through the AZURE_* environment variables, or a developer sign-in).
/// </summary>
public sealed class AzureOptions
{
    public const string SectionName = "Azure";

    /// <summary>Blob service endpoint, for example https://contosoinvoices.blob.core.windows.net.</summary>
    public string BlobServiceUri { get; set; } = string.Empty;

    public string IntakeContainer { get; set; } = "invoice-intake";

    /// <summary>Fully qualified namespace, for example contoso-invoices.servicebus.windows.net.</summary>
    public string ServiceBusNamespace { get; set; } = string.Empty;

    public string InvoiceReceivedQueue { get; set; } = "invoice-received";

    public string CommandsQueue { get; set; } = "exchange-commands";

    public string CommandResultsQueue { get; set; } = "exchange-command-results";

    /// <summary>AMQP over WebSockets on TCP 443 instead of AMQP on TCP 5671, for networks that only open 443.</summary>
    public bool UseWebSockets { get; set; }
}
