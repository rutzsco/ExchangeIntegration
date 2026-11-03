using ExchangeIntegration.Adapter;
using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Tests.TestSupport;

/// <summary>An in-memory mailbox. Every identifier and address is fake.</summary>
public sealed class FakeMailbox : IExchangeMailbox
{
    private readonly Dictionary<string, EmailMessage> _messages = [];
    private readonly Dictionary<string, byte[]> _attachments = [];
    private readonly List<InboxItem> _inbox = [];

    public List<(EmailMessage Original, string BodyText)> SentReplies { get; } = [];

    public List<(IReadOnlyList<string> ItemIds, string Folder)> Moves { get; } = [];

    public int GetMessageCalls { get; private set; }

    public Exception? ListInboxFailure { get; set; }

    public Exception? SendReplyFailure { get; set; }

    public Exception? MoveFailure { get; set; }

    /// <summary>Message IDs whose GetMessage call fails.</summary>
    public Dictionary<string, Exception> GetMessageFailures { get; } = [];

    public EmailMessage AddEmail(
        string internetMessageId,
        string from = "supplier@example.test",
        string? subject = "Invoice 1001",
        string? autoSubmitted = null,
        string itemClass = "IPM.Note",
        params (string Name, string? ContentType, byte[] Content)[] files)
    {
        var itemId = $"item-{_inbox.Count + 1}";
        var received = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero).AddMinutes(_inbox.Count);
        var attachments = new List<EmailAttachment>();
        foreach (var (name, contentType, content) in files)
        {
            var attachmentId = $"{itemId}-att-{attachments.Count + 1}";
            _attachments[attachmentId] = content;
            attachments.Add(new EmailAttachment(attachmentId, name, contentType, content.Length, IsInline: false, IsFile: true));
        }

        var message = new EmailMessage(
            itemId,
            internetMessageId,
            subject,
            new EmailAddress(from, "SMTP"),
            received,
            References: null,
            autoSubmitted,
            attachments);

        _messages[itemId] = message;
        _inbox.Add(new InboxItem(itemId, itemClass, internetMessageId, received));
        return message;
    }

    public void AddItemAttachment(string itemId, string name)
    {
        var message = _messages[itemId];
        _messages[itemId] = message with
        {
            Attachments = [.. message.Attachments, new EmailAttachment($"{itemId}-item", name, null, 10, false, IsFile: false)],
        };
    }

    public void AddInboxItemWithoutMessageId() =>
        _inbox.Add(new InboxItem($"item-{_inbox.Count + 1}", "IPM.Note", null, DateTimeOffset.UnixEpoch));

    public void RemoveFromInbox(string internetMessageId) => _inbox.RemoveAll(item => item.InternetMessageId == internetMessageId);

    public Task<IReadOnlyList<InboxItem>> ListInboxAsync(CancellationToken cancellationToken) =>
        ListInboxFailure is null ? Task.FromResult<IReadOnlyList<InboxItem>>(_inbox.ToList()) : Task.FromException<IReadOnlyList<InboxItem>>(ListInboxFailure);

    public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
    {
        GetMessageCalls++;
        var message = _messages[itemId];
        return message.InternetMessageId is { } id && GetMessageFailures.TryGetValue(id, out var failure)
            ? Task.FromException<EmailMessage>(failure)
            : Task.FromResult(message);
    }

    public Task<byte[]> GetAttachmentContentAsync(string attachmentId, CancellationToken cancellationToken) =>
        Task.FromResult(_attachments[attachmentId]);

    public Task<IReadOnlyList<string>> FindInboxItemIdsAsync(string internetMessageId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_inbox.Where(item => item.InternetMessageId == internetMessageId).Select(item => item.ItemId).ToList());

    public Task SendReplyAsync(EmailMessage original, string bodyText, CancellationToken cancellationToken)
    {
        if (SendReplyFailure is not null)
        {
            return Task.FromException(SendReplyFailure);
        }

        SentReplies.Add((original, bodyText));
        return Task.CompletedTask;
    }

    public Task MoveAsync(IReadOnlyList<string> itemIds, string folderName, CancellationToken cancellationToken)
    {
        if (MoveFailure is not null)
        {
            return Task.FromException(MoveFailure);
        }

        Moves.Add((itemIds, folderName));
        _inbox.RemoveAll(item => itemIds.Contains(item.ItemId));
        return Task.CompletedTask;
    }
}

/// <summary>Blob Storage in memory, with the same "announced" metadata semantics as <c>BlobInvoiceStore</c>.</summary>
public sealed class InMemoryInvoiceStore : IInvoiceStore
{
    public static readonly Uri ContainerUri = new("https://contosoinvoices.blob.core.windows.net/invoice-intake/");

    public Dictionary<string, (BinaryData Content, string ContentType)> Blobs { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Announced { get; } = new(StringComparer.Ordinal);

    public int IsAnnouncedCalls { get; private set; }

    public Exception? UploadFailure { get; set; }

    public Task<Uri> UploadAsync(string blobName, BinaryData content, string contentType, CancellationToken cancellationToken)
    {
        if (UploadFailure is not null)
        {
            return Task.FromException<Uri>(UploadFailure);
        }

        Blobs[blobName] = (content, contentType);
        return Task.FromResult(new Uri(ContainerUri, blobName));
    }

    public Task<BinaryData?> DownloadAsync(string blobName, CancellationToken cancellationToken) =>
        Task.FromResult(Blobs.TryGetValue(blobName, out var blob) ? blob.Content : null);

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        Blobs.Remove(blobName);
        return Task.CompletedTask;
    }

    public Task<bool> IsAnnouncedAsync(string messageKey, CancellationToken cancellationToken)
    {
        IsAnnouncedCalls++;
        return Task.FromResult(Announced.Contains(messageKey));
    }

    public Task MarkAnnouncedAsync(string messageKey, DateTimeOffset announcedUtc, CancellationToken cancellationToken)
    {
        if (!Blobs.ContainsKey(BlobPaths.Manifest(messageKey)))
        {
            throw new InvalidOperationException("The manifest must exist before it is marked announced.");
        }

        Announced.Add(messageKey);
        return Task.CompletedTask;
    }
}

public sealed class RecordingEngineQueues : IEngineQueues
{
    public List<InvoiceReceivedMessage> InvoicesReceived { get; } = [];

    public List<CommandResultMessage> Results { get; } = [];

    public Exception? PublishInvoiceFailure { get; set; }

    public Task PublishInvoiceReceivedAsync(InvoiceReceivedMessage message, CancellationToken cancellationToken)
    {
        if (PublishInvoiceFailure is not null)
        {
            return Task.FromException(PublishInvoiceFailure);
        }

        InvoicesReceived.Add(message);
        return Task.CompletedTask;
    }

    public Task PublishCommandResultAsync(CommandResultMessage result, CancellationToken cancellationToken)
    {
        Results.Add(result);
        return Task.CompletedTask;
    }
}

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
