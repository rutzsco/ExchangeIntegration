using Azure.Messaging.ServiceBus;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Cloud;

/// <summary>
/// Sends to the invoice-received and exchange-command-results queues. invoice.received uses the messageKey as its
/// MessageId so duplicate detection on that queue drops a repeat after a crash.
/// </summary>
public sealed class ServiceBusEngineQueues : IEngineQueues, IAsyncDisposable
{
    private const string Json = "application/json";

    private readonly ServiceBusSender _invoiceReceived;
    private readonly ServiceBusSender _results;

    public ServiceBusEngineQueues(ServiceBusClient client, IOptions<AzureOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _invoiceReceived = client.CreateSender(options.Value.InvoiceReceivedQueue);
        _results = client.CreateSender(options.Value.CommandResultsQueue);
    }

    public Task PublishInvoiceReceivedAsync(InvoiceReceivedMessage message, CancellationToken cancellationToken) =>
        _invoiceReceived.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(message, AdapterJson.Options))
            {
                MessageId = message.MessageKey,
                Subject = message.Type,
                ContentType = Json,
            },
            cancellationToken);

    public Task PublishCommandResultAsync(CommandResultMessage result, CancellationToken cancellationToken) =>
        _results.SendMessageAsync(
            new ServiceBusMessage(BinaryData.FromObjectAsJson(result, AdapterJson.Options))
            {
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = result.CommandId,
                Subject = result.Type,
                ContentType = Json,
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _invoiceReceived.DisposeAsync();
        await _results.DisposeAsync();
    }
}
