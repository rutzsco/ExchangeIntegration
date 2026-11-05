using ExchangeIntegration.Adapter;

namespace ExchangeIntegration.Local;

/// <summary>
/// Blob Storage for local mode: each blob is a file under the container folder. The "announced" checkpoint is a
/// manifest.json.announced file beside the manifest, and overwriting a blob removes it, as it clears blob metadata.
/// </summary>
public sealed class FileInvoiceStore : IInvoiceStore
{
    public const string AnnouncedSuffix = ".announced";

    private readonly string _root;

    public FileInvoiceStore(string containerFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerFolder);
        _root = Path.GetFullPath(containerFolder);
    }

    public async Task<Uri> UploadAsync(string blobName, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathOf(blobName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
        File.Delete(path + AnnouncedSuffix);
        return new Uri(path);
    }

    public async Task<BinaryData?> DownloadAsync(string blobName, CancellationToken cancellationToken)
    {
        var path = PathOf(blobName);
        return File.Exists(path) ? BinaryData.FromBytes(await File.ReadAllBytesAsync(path, cancellationToken)) : null;
    }

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        var path = PathOf(blobName);
        if (File.Exists(path))
        {
            File.Delete(path);
            File.Delete(path + AnnouncedSuffix);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsAnnouncedAsync(string messageKey, CancellationToken cancellationToken)
    {
        var manifest = PathOf(BlobPaths.Manifest(messageKey));
        return Task.FromResult(File.Exists(manifest) && File.Exists(manifest + AnnouncedSuffix));
    }

    public Task MarkAnnouncedAsync(string messageKey, DateTimeOffset announcedUtc, CancellationToken cancellationToken)
    {
        var manifest = PathOf(BlobPaths.Manifest(messageKey));
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException("The manifest must exist before it is marked announced.");
        }

        return File.WriteAllTextAsync(manifest + AnnouncedSuffix, announcedUtc.UtcDateTime.ToString("O"), cancellationToken);
    }

    private string PathOf(string blobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        var path = Path.GetFullPath(Path.Combine(_root, blobName));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new ArgumentException("The blob name must stay inside the container folder.", nameof(blobName));
    }
}
