namespace ExchangeIntegration.Exchange;

/// <summary>An Inbox entry as listed by FindItem: just enough to decide whether it is new.</summary>
public sealed record InboxItem(string ItemId, string ItemClass, string? InternetMessageId, DateTimeOffset ReceivedUtc);

/// <summary>A sender or recipient address. RoutingType is usually SMTP.</summary>
public sealed record EmailAddress(string Address, string? RoutingType);

/// <summary>Attachment metadata from GetItem. Only file attachments have downloadable content.</summary>
public sealed record EmailAttachment(string? AttachmentId, string Name, string? ContentType, long Size, bool IsInline, bool IsFile);

/// <summary>The fields of one email that the adapter uses. Subject and sender are untrusted and never logged.</summary>
public sealed record EmailMessage(
    string ItemId,
    string? InternetMessageId,
    string? Subject,
    EmailAddress? From,
    DateTimeOffset ReceivedUtc,
    string? References,
    string? AutoSubmitted,
    IReadOnlyList<EmailAttachment> Attachments);

/// <summary>
/// The mailbox operations of service mode, all against the configured mailbox. Every failure surfaces as an
/// <see cref="EwsOperationException"/>.
/// </summary>
public interface IExchangeMailbox
{
    /// <summary>All items in the Inbox, oldest first.</summary>
    Task<IReadOnlyList<InboxItem>> ListInboxAsync(CancellationToken cancellationToken);

    Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken);

    Task<byte[]> GetAttachmentContentAsync(string attachmentId, CancellationToken cancellationToken);

    /// <summary>The Inbox items whose Message-ID header equals <paramref name="internetMessageId"/> (normally one).</summary>
    Task<IReadOnlyList<string>> FindInboxItemIdsAsync(string internetMessageId, CancellationToken cancellationToken);

    /// <summary>Sends a new plain-text message to the original From address, threaded under the original email.</summary>
    Task SendReplyAsync(EmailMessage original, string bodyText, CancellationToken cancellationToken);

    /// <summary>Moves the items to a top-level folder (beside the Inbox) with the given display name.</summary>
    Task MoveAsync(IReadOnlyList<string> itemIds, string folderName, CancellationToken cancellationToken);
}

/// <summary>An EWS call in service mode failed. The message is already sanitized.</summary>
public sealed class EwsOperationException : Exception
{
    public EwsOperationException(
        string operation,
        FailureCategory category,
        string message,
        string? responseCode = null,
        bool isTransient = false,
        bool mayHaveTakenEffect = false)
        : base(Redaction.SanitizeMessage(message))
    {
        Operation = operation;
        Category = category;
        ResponseCode = responseCode;
        IsTransient = isTransient;
        MayHaveTakenEffect = mayHaveTakenEffect;
    }

    public string Operation { get; }

    public FailureCategory Category { get; }

    /// <summary>The EWS response code, such as ErrorAccessDenied, when Exchange returned one.</summary>
    public string? ResponseCode { get; }

    /// <summary>Trying again later may succeed (connectivity, timeout, server busy).</summary>
    public bool IsTransient { get; }

    /// <summary>
    /// The request may have reached Exchange and been carried out even though no answer came back (a timeout or a
    /// broken connection). Exchange error responses and refusals are known not to have taken effect.
    /// </summary>
    public bool MayHaveTakenEffect { get; }
}
