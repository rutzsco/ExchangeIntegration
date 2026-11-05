using ExchangeIntegration.Exchange;
using MimeKit;
using MimeKit.Utils;

namespace ExchangeIntegration.Local;

/// <summary>
/// A mailbox kept in a folder for local mode. Every .eml file in Inbox is an email; a reply is written to Sent Items
/// and a move moves the file to a sibling folder, which must exist (as a top-level folder must in Exchange). Item IDs
/// are file names and attachment IDs are "&lt;file name&gt;|&lt;index&gt;".
/// </summary>
public sealed class FolderMailbox : IExchangeMailbox
{
    public const string InboxFolder = "Inbox";
    public const string SentItemsFolder = "Sent Items";

    private readonly string _folder;
    private readonly string _address;
    private readonly TimeProvider _time;

    public FolderMailbox(string folder, string address, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(time);

        _folder = folder;
        _address = address.Trim();
        _time = time;
    }

    public string InboxPath => Path.Combine(_folder, InboxFolder);

    public Task<IReadOnlyList<InboxItem>> ListInboxAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InboxItem>>(
            ReadInbox(cancellationToken)
                .OrderBy(item => item.ReceivedUtc)
                .ThenBy(item => item.ItemId, StringComparer.Ordinal)
                .ToList());

    public Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
    {
        var path = InboxFile(itemId, "GetItem");
        using var message = Load(path, "GetItem", cancellationToken);

        var attachments = message.Attachments
            .Select((entity, index) => entity is MimePart part
                ? new EmailAttachment($"{itemId}|{index}", part.FileName ?? $"attachment-{index + 1}", part.ContentType.MimeType, Decode(part).LongLength, IsInline: false, IsFile: true)
                : new EmailAttachment(null, (entity as MessagePart)?.Message?.Subject ?? "attached item", entity.ContentType.MimeType, 0, IsInline: false, IsFile: false))
            .ToList();

        var sender = message.From.Mailboxes.FirstOrDefault();
        return Task.FromResult(new EmailMessage(
            itemId,
            MessageIdOf(message),
            message.Subject,
            sender is null ? null : new EmailAddress(sender.Address, "SMTP"),
            ReceivedUtc(message, path),
            message.Headers[HeaderId.References],
            message.Headers["Auto-Submitted"],
            attachments));
    }

    public Task<byte[]> GetAttachmentContentAsync(string attachmentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        var separator = attachmentId.LastIndexOf('|');
        if (separator <= 0 || !int.TryParse(attachmentId[(separator + 1)..], out var index))
        {
            throw NotFound("GetAttachment");
        }

        using var message = Load(InboxFile(attachmentId[..separator], "GetAttachment"), "GetAttachment", cancellationToken);
        return message.Attachments.ElementAtOrDefault(index) is MimePart part
            ? Task.FromResult(Decode(part))
            : throw NotFound("GetAttachment");
    }

    public Task<IReadOnlyList<string>> FindInboxItemIdsAsync(string internetMessageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internetMessageId);

        return Task.FromResult<IReadOnlyList<string>>(
            ReadInbox(cancellationToken)
                .Where(item => string.Equals(item.InternetMessageId, internetMessageId.Trim(), StringComparison.Ordinal))
                .Select(item => item.ItemId)
                .ToList());
    }

    public async Task SendReplyAsync(EmailMessage original, string bodyText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyText);
        if (original.From is null)
        {
            throw new ArgumentException("The original email has no sender to reply to.", nameof(original));
        }

        var subject = original.Subject ?? string.Empty;
        if (!subject.StartsWith("RE:", StringComparison.OrdinalIgnoreCase))
        {
            subject = "RE: " + subject;
        }

        var now = _time.GetUtcNow();
        using var reply = new MimeMessage();
        try
        {
            reply.From.Add(new MailboxAddress(null, _address));
            reply.To.Add(new MailboxAddress(null, original.From.Address));
        }
        catch (ParseException)
        {
            throw new EwsOperationException("CreateItem", FailureCategory.EwsError, "The recipient address is not valid.", "ErrorInvalidRecipients");
        }

        reply.Subject = subject;
        reply.Date = now;
        reply.MessageId = MimeUtils.GenerateMessageId(_address[(_address.IndexOf('@') + 1)..]);
        if (original.InternetMessageId is not null)
        {
            reply.Headers[HeaderId.InReplyTo] = original.InternetMessageId;
        }

        var references = string.Join(' ', new[] { original.References, original.InternetMessageId }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (references.Length > 0)
        {
            reply.Headers[HeaderId.References] = references;
        }

        reply.Body = new TextPart("plain") { Text = bodyText };

        try
        {
            var sentItems = Directory.CreateDirectory(Path.Combine(_folder, SentItemsFolder)).FullName;
            await reply.WriteToAsync(Path.Combine(sentItems, $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml"), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileFailure("CreateItem", exception, mayHaveTakenEffect: true);
        }
    }

    public Task MoveAsync(IReadOnlyList<string> itemIds, string folderName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        var name = folderName.Trim();
        var target = Path.Combine(_folder, name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || !Directory.Exists(target))
        {
            throw new EwsOperationException(
                "FindFolder",
                FailureCategory.EwsError,
                $"The mailbox has no top-level folder named '{name}'; create it beside the Inbox.",
                "ErrorFolderNotFound");
        }

        foreach (var itemId in itemIds)
        {
            var source = InboxFile(itemId, "MoveItem");
            try
            {
                File.Move(source, UniquePath(target, Path.GetFileName(source)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw FileFailure("MoveItem", exception, mayHaveTakenEffect: false);
            }
        }

        return Task.CompletedTask;
    }

    private IEnumerable<InboxItem> ReadInbox(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(InboxPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(InboxPath, "*.eml"))
        {
            MimeMessage message;
            try
            {
                message = MimeMessage.Load(path, cancellationToken);
            }
            catch (IOException)
            {
                // Still being copied in (or just removed); the next poll sees it.
                continue;
            }

            using (message)
            {
                yield return new InboxItem(Path.GetFileName(path), "IPM.Note", MessageIdOf(message), ReceivedUtc(message, path));
            }
        }
    }

    private string InboxFile(string itemId, string operation)
    {
        var name = Path.GetFileName(itemId ?? string.Empty);
        var path = Path.Combine(InboxPath, name);
        return name.Length > 0 && name == itemId && File.Exists(path) ? path : throw NotFound(operation);
    }

    private static MimeMessage Load(string path, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return MimeMessage.Load(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            throw NotFound(operation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileFailure(operation, exception, mayHaveTakenEffect: false);
        }
    }

    // MimeKit drops the angle brackets; Exchange's InternetMessageId keeps them.
    private static string? MessageIdOf(MimeMessage message) =>
        string.IsNullOrWhiteSpace(message.MessageId) ? null : $"<{message.MessageId.Trim()}>";

    private static DateTimeOffset ReceivedUtc(MimeMessage message, string path) =>
        message.Date != DateTimeOffset.MinValue ? message.Date.ToUniversalTime() : new DateTimeOffset(File.GetLastWriteTimeUtc(path));

    private static byte[] Decode(MimePart part)
    {
        using var content = new MemoryStream();
        part.Content?.DecodeTo(content);
        return content.ToArray();
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        for (var copy = 1; File.Exists(path); copy++)
        {
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(fileName)}-{copy}{Path.GetExtension(fileName)}");
        }

        return path;
    }

    private static EwsOperationException NotFound(string operation) =>
        new(operation, FailureCategory.EwsError, "The item is not in the Inbox folder.", "ErrorItemNotFound");

    private static EwsOperationException FileFailure(string operation, Exception exception, bool mayHaveTakenEffect) =>
        new(operation, FailureCategory.Connectivity, $"The local mailbox folder could not be used: {exception.Message}", isTransient: true, mayHaveTakenEffect: mayHaveTakenEffect);
}
