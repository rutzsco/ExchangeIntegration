using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Adapter;

/// <summary>What one poll did.</summary>
public sealed record PollSummary(int InboxItems, int Captured, int AlreadyAnnounced, int Failed, int Deferred);

/// <summary>
/// Reads the Inbox and captures every email not yet announced: attachments to Blob Storage, then the manifest, then
/// invoice.received, then the "announced" checkpoint on the manifest. Emails stay in the Inbox until the Engine moves
/// them. A crash between steps repeats the capture on the next poll; the Engine sees at most a duplicate
/// invoice.received, which Service Bus duplicate detection (MessageId = messageKey) normally absorbs.
/// </summary>
public sealed class InvoiceIngestion
{
    private const string OctetStream = "application/octet-stream";

    private readonly IExchangeMailbox _mailbox;
    private readonly IInvoiceStore _store;
    private readonly IEngineQueues _queues;
    private readonly ExchangeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InvoiceIngestion> _logger;

    // Keys known to be announced, so a steady Inbox costs one FindItem per poll rather than one blob read per email.
    private HashSet<string> _announced = new(StringComparer.Ordinal);

    public InvoiceIngestion(
        IExchangeMailbox mailbox,
        IInvoiceStore store,
        IEngineQueues queues,
        IOptions<ExchangeOptions> options,
        TimeProvider time,
        ILogger<InvoiceIngestion> logger)
    {
        _mailbox = mailbox;
        _store = store;
        _queues = queues;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<PollSummary> PollOnceAsync(CancellationToken cancellationToken)
    {
        var inbox = await _mailbox.ListInboxAsync(cancellationToken);

        var candidates = new List<(InboxItem Item, string Key)>();
        var withoutMessageId = 0;
        foreach (var item in inbox)
        {
            if (!item.ItemClass.StartsWith("IPM.Note", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.InternetMessageId))
            {
                withoutMessageId++;
                continue;
            }

            candidates.Add((item, MessageKeys.Compute(_options.Mailbox, item.InternetMessageId)));
        }

        if (withoutMessageId > 0)
        {
            _logger.LogWarning(
                "{Count} Inbox emails have no Message-ID header and are left alone; move or delete them by hand.",
                withoutMessageId);
        }

        int captured = 0, alreadyAnnounced = 0, failed = 0, deferred = 0;
        foreach (var (item, key) in candidates)
        {
            if (_announced.Contains(key))
            {
                alreadyAnnounced++;
                continue;
            }

            if (captured >= _options.MaxMessagesPerPoll)
            {
                deferred++;
                continue;
            }

            try
            {
                if (await _store.IsAnnouncedAsync(key, cancellationToken))
                {
                    _announced.Add(key);
                    alreadyAnnounced++;
                    continue;
                }

                await CaptureAsync(item, key, cancellationToken);
                _announced.Add(key);
                captured++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                _logger.LogError(
                    "Capturing email {MessageKey} failed and will be retried next poll: {Error}",
                    key,
                    Describe(exception));
            }
        }

        // Forget emails that have left the Inbox so the set does not grow forever.
        _announced = candidates.Select(candidate => candidate.Key).Where(_announced.Contains).ToHashSet(StringComparer.Ordinal);

        var summary = new PollSummary(inbox.Count, captured, alreadyAnnounced, failed, deferred);
        if (captured > 0 || failed > 0 || deferred > 0)
        {
            _logger.LogInformation(
                "Poll: {InboxItems} Inbox items, {Captured} captured, {AlreadyAnnounced} already announced, {Failed} failed, {Deferred} deferred to the next poll.",
                summary.InboxItems,
                summary.Captured,
                summary.AlreadyAnnounced,
                summary.Failed,
                summary.Deferred);
        }
        else
        {
            _logger.LogDebug("Poll: {InboxItems} Inbox items, nothing new.", summary.InboxItems);
        }

        return summary;
    }

    private async Task CaptureAsync(InboxItem item, string key, CancellationToken cancellationToken)
    {
        var message = await _mailbox.GetMessageAsync(item.ItemId, cancellationToken);

        var files = new List<ManifestFile>();
        var skipped = 0;
        var index = 0;
        foreach (var attachment in message.Attachments)
        {
            if (!attachment.IsFile || string.IsNullOrEmpty(attachment.AttachmentId))
            {
                skipped++;
                continue;
            }

            index++;
            var content = await _mailbox.GetAttachmentContentAsync(attachment.AttachmentId, cancellationToken);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? OctetStream : attachment.ContentType;
            var blobName = BlobPaths.Attachment(key, index, attachment.Name);
            await _store.UploadAsync(blobName, BinaryData.FromBytes(content), contentType, cancellationToken);

            files.Add(new ManifestFile
            {
                Name = attachment.Name,
                Blob = blobName,
                ContentType = contentType,
                Size = content.LongLength,
                IsInline = attachment.IsInline,
            });
        }

        var internetMessageId = item.InternetMessageId!;
        var receivedUtc = message.ReceivedUtc == DateTimeOffset.MinValue ? item.ReceivedUtc : message.ReceivedUtc;
        var manifest = new InvoiceManifest
        {
            MessageKey = key,
            Mailbox = _options.Mailbox,
            InternetMessageId = internetMessageId,
            ReceivedUtc = receivedUtc,
            From = message.From?.Address,
            Subject = message.Subject,
            Files = files,
            SkippedAttachments = skipped,
            CapturedUtc = _time.GetUtcNow(),
        };

        // The manifest goes last so its presence means every file it lists is stored.
        var manifestUri = await _store.UploadAsync(
            BlobPaths.Manifest(key),
            BinaryData.FromObjectAsJson(manifest, AdapterJson.Options),
            "application/json",
            cancellationToken);

        await _queues.PublishInvoiceReceivedAsync(
            new InvoiceReceivedMessage
            {
                MessageKey = key,
                Mailbox = _options.Mailbox,
                InternetMessageId = internetMessageId,
                ReceivedUtc = receivedUtc,
                Manifest = manifestUri,
            },
            cancellationToken);

        await _store.MarkAnnouncedAsync(key, _time.GetUtcNow(), cancellationToken);

        _logger.LogInformation(
            "Captured email {MessageKey}: {Files} files stored, {Skipped} attachments skipped, invoice.received sent.",
            key,
            files.Count,
            skipped);
    }

    internal static string Describe(Exception exception) => exception switch
    {
        EwsOperationException ews => $"EWS {ews.Operation} {ews.Category}{(ews.ResponseCode is null ? string.Empty : $" ({ews.ResponseCode})")}: {ews.Message}",
        _ => $"{exception.GetType().Name}: {Redaction.SanitizeMessage(exception.Message)}",
    };
}
