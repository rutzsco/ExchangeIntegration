using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ExchangeIntegration.Adapter;

namespace ExchangeIntegration.Cloud;

/// <summary>The intake container. The "announced" checkpoint is the announcedutc metadata on manifest.json.</summary>
public sealed class BlobInvoiceStore : IInvoiceStore
{
    public const string AnnouncedMetadataKey = "announcedutc";

    private readonly BlobContainerClient _container;

    public BlobInvoiceStore(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    public async Task<Uri> UploadAsync(string blobName, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        var blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
        return blob.Uri;
    }

    public async Task<BinaryData?> DownloadAsync(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _container.GetBlobClient(blobName).DownloadContentAsync(cancellationToken);
            return result.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
        _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    public async Task<bool> IsAnnouncedAsync(string messageKey, CancellationToken cancellationToken)
    {
        try
        {
            var properties = await _container.GetBlobClient(BlobPaths.Manifest(messageKey)).GetPropertiesAsync(cancellationToken: cancellationToken);
            return properties.Value.Metadata.ContainsKey(AnnouncedMetadataKey);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return false;
        }
    }

    public Task MarkAnnouncedAsync(string messageKey, DateTimeOffset announcedUtc, CancellationToken cancellationToken) =>
        _container.GetBlobClient(BlobPaths.Manifest(messageKey)).SetMetadataAsync(
            new Dictionary<string, string> { [AnnouncedMetadataKey] = announcedUtc.UtcDateTime.ToString("O") },
            cancellationToken: cancellationToken);
}
