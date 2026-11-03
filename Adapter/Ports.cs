namespace ExchangeIntegration.Adapter;

/// <summary>The intake container in Blob Storage (files, manifests and command records).</summary>
public interface IInvoiceStore
{
    /// <summary>Creates or overwrites a blob and returns its URL.</summary>
    Task<Uri> UploadAsync(string blobName, BinaryData content, string contentType, CancellationToken cancellationToken);

    /// <summary>The blob's content, or null when it does not exist.</summary>
    Task<BinaryData?> DownloadAsync(string blobName, CancellationToken cancellationToken);

    Task DeleteAsync(string blobName, CancellationToken cancellationToken);

    /// <summary>True once the email's invoice.received message was sent (the "saved place" checkpoint).</summary>
    Task<bool> IsAnnouncedAsync(string messageKey, CancellationToken cancellationToken);

    /// <summary>Marks the manifest as announced, after invoice.received was sent.</summary>
    Task MarkAnnouncedAsync(string messageKey, DateTimeOffset announcedUtc, CancellationToken cancellationToken);
}

/// <summary>The two Service Bus queues the adapter sends to.</summary>
public interface IEngineQueues
{
    Task PublishInvoiceReceivedAsync(InvoiceReceivedMessage message, CancellationToken cancellationToken);

    Task PublishCommandResultAsync(CommandResultMessage result, CancellationToken cancellationToken);
}
