using System.Text.Json;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Local;

/// <summary>Service Bus for local mode: every message the adapter sends is an indented JSON file in the queue's folder.</summary>
public sealed class FileEngineQueues : IEngineQueues
{
    private static readonly JsonSerializerOptions Indented = new(AdapterJson.Options) { WriteIndented = true };

    private readonly string _invoiceReceived;
    private readonly string _results;
    private readonly TimeProvider _time;

    public FileEngineQueues(LocalFolders folders, IOptions<AzureOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(options);

        _invoiceReceived = folders.Queue(options.Value.InvoiceReceivedQueue);
        _results = folders.Queue(options.Value.CommandResultsQueue);
        _time = time;
    }

    // Named by messageKey (the Service Bus MessageId), so a repeat after a crash lands on the same file, as duplicate
    // detection would drop it.
    public Task PublishInvoiceReceivedAsync(InvoiceReceivedMessage message, CancellationToken cancellationToken) =>
        WriteAsync(_invoiceReceived, $"{BlobPaths.SafeFileName(message.MessageKey)}.json", message, cancellationToken);

    public Task PublishCommandResultAsync(CommandResultMessage result, CancellationToken cancellationToken) =>
        WriteAsync(_results, $"{_time.GetUtcNow():yyyyMMdd-HHmmssfff}-{BlobPaths.SafeFileName(result.CommandId)}.json", result, cancellationToken);

    private static async Task WriteAsync<T>(string folder, string fileName, T message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, fileName), JsonSerializer.SerializeToUtf8Bytes(message, Indented), cancellationToken);
    }
}
