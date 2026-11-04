using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Xml.Linq;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Exchange;

/// <summary>
/// The service-mode mailbox over EWS, with the same Windows authentication and redirect policy as the probe. It never
/// logs request or response bodies, subjects, senders or item identifiers.
/// </summary>
public sealed class EwsMailbox : IExchangeMailbox
{
    public const int PageSize = 200;
    public const int MaxPages = 50;

    private static readonly XNamespace Soap = EwsRequestBuilder.SoapNamespaceName;
    private static readonly XNamespace M = EwsRequestBuilder.MessagesNamespaceName;
    private static readonly XNamespace T = EwsRequestBuilder.TypesNamespaceName;

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "ErrorServerBusy",
        "ErrorInternalServerTransientError",
        "ErrorTimeoutExpired",
        "ErrorMailboxStoreUnavailable",
        "ErrorConnectionFailed",
        "ErrorMailboxMoveInProgress",
    };

    private readonly HttpClient _httpClient;
    private readonly ExchangeOptions _options;
    private readonly ILogger<EwsMailbox> _logger;
    private readonly ConcurrentDictionary<string, string> _folderIds = new(StringComparer.OrdinalIgnoreCase);

    public EwsMailbox(
        HttpClient httpClient,
        IOptions<ExchangeOptions> options,
        ILogger<EwsMailbox> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<InboxItem>> ListInboxAsync(CancellationToken cancellationToken)
    {
        var items = new List<InboxItem>();
        var offset = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var request = new XElement(
                M + "FindItem",
                new XAttribute("Traversal", "Shallow"),
                new XElement(
                    M + "ItemShape",
                    new XElement(T + "BaseShape", "IdOnly"),
                    new XElement(
                        T + "AdditionalProperties",
                        Field("item:ItemClass"),
                        Field("item:DateTimeReceived"),
                        Field("message:InternetMessageId"))),
                PageView(PageSize, offset),
                new XElement(
                    M + "SortOrder",
                    new XElement(T + "FieldOrder", new XAttribute("Order", "Ascending"), Field("item:DateTimeReceived"))),
                new XElement(M + "ParentFolderIds", EwsRequestBuilder.DistinguishedFolder("inbox", _options.Mailbox)));

            var response = await InvokeSingleAsync("FindItem", request, cancellationToken);
            var root = response.Element(M + "RootFolder")
                ?? throw Protocol("FindItem", "The response has no RootFolder.");

            foreach (var item in root.Element(T + "Items")?.Elements() ?? [])
            {
                var id = (string?)item.Element(T + "ItemId")?.Attribute("Id");
                if (!string.IsNullOrEmpty(id))
                {
                    items.Add(new InboxItem(
                        id,
                        Text(item, "ItemClass") ?? string.Empty,
                        Text(item, "InternetMessageId"),
                        ReadDate(item)));
                }
            }

            if ((bool?)root.Attribute("IncludesLastItemInRange") ?? true)
            {
                return items;
            }

            var next = (int?)root.Attribute("IndexedPagingOffset");
            if (next is not { } nextOffset || nextOffset <= offset)
            {
                return items;
            }

            offset = nextOffset;
        }

        _logger.LogWarning(
            "The Inbox listing stopped after {Pages} pages of {PageSize} items; the oldest are handled first and the rest wait.",
            MaxPages,
            PageSize);
        return items;
    }

    public async Task<EmailMessage> GetMessageAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var request = new XElement(
            M + "GetItem",
            new XElement(
                M + "ItemShape",
                new XElement(T + "BaseShape", "IdOnly"),
                new XElement(
                    T + "AdditionalProperties",
                    Field("item:Subject"),
                    Field("item:DateTimeReceived"),
                    Field("item:Attachments"),
                    Field("item:InternetMessageHeaders"),
                    Field("message:From"),
                    Field("message:InternetMessageId"),
                    Field("message:References"))),
            new XElement(M + "ItemIds", new XElement(T + "ItemId", new XAttribute("Id", itemId))));

        var response = await InvokeSingleAsync("GetItem", request, cancellationToken);
        var item = response.Element(M + "Items")?.Elements().FirstOrDefault()
            ?? throw Protocol("GetItem", "The response contains no item.");

        var fromMailbox = item.Element(T + "From")?.Element(T + "Mailbox");
        var fromAddress = fromMailbox is null ? null : Text(fromMailbox, "EmailAddress");

        var autoSubmitted = item.Element(T + "InternetMessageHeaders")?
            .Elements(T + "InternetMessageHeader")
            .FirstOrDefault(header => string.Equals((string?)header.Attribute("HeaderName"), "Auto-Submitted", StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();

        var attachments = (item.Element(T + "Attachments")?.Elements() ?? [])
            .Select(attachment => new EmailAttachment(
                (string?)attachment.Element(T + "AttachmentId")?.Attribute("Id"),
                Text(attachment, "Name") ?? "attachment",
                Text(attachment, "ContentType"),
                (long?)attachment.Element(T + "Size") ?? 0,
                (bool?)attachment.Element(T + "IsInline") ?? false,
                attachment.Name == T + "FileAttachment"))
            .ToList();

        return new EmailMessage(
            itemId,
            Text(item, "InternetMessageId"),
            Text(item, "Subject"),
            fromAddress is null ? null : new EmailAddress(fromAddress, Text(fromMailbox!, "RoutingType")),
            ReadDate(item),
            Text(item, "References"),
            autoSubmitted,
            attachments);
    }

    public async Task<byte[]> GetAttachmentContentAsync(string attachmentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        var request = new XElement(
            M + "GetAttachment",
            new XElement(M + "AttachmentIds", new XElement(T + "AttachmentId", new XAttribute("Id", attachmentId))));

        var response = await InvokeSingleAsync("GetAttachment", request, cancellationToken);
        var content = response.Element(M + "Attachments")?.Elements().FirstOrDefault()?.Element(T + "Content")?.Value
            ?? throw Protocol("GetAttachment", "The attachment has no content.");

        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            throw Protocol("GetAttachment", "The attachment content is not valid base64.");
        }
    }

    public async Task<IReadOnlyList<string>> FindInboxItemIdsAsync(string internetMessageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internetMessageId);

        var request = new XElement(
            M + "FindItem",
            new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly")),
            PageView(10, 0),
            new XElement(
                M + "Restriction",
                new XElement(
                    T + "IsEqualTo",
                    Field("message:InternetMessageId"),
                    new XElement(T + "FieldURIOrConstant", new XElement(T + "Constant", new XAttribute("Value", internetMessageId))))),
            new XElement(
                M + "SortOrder",
                new XElement(T + "FieldOrder", new XAttribute("Order", "Ascending"), Field("item:DateTimeReceived"))),
            new XElement(M + "ParentFolderIds", EwsRequestBuilder.DistinguishedFolder("inbox", _options.Mailbox)));

        var response = await InvokeSingleAsync("FindItem", request, cancellationToken);
        return (response.Element(M + "RootFolder")?.Element(T + "Items")?.Elements() ?? [])
            .Select(item => (string?)item.Element(T + "ItemId")?.Attribute("Id"))
            .OfType<string>()
            .Where(id => id.Length > 0)
            .ToList();
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

        var references = string.Join(' ', new[] { original.References, original.InternetMessageId }.Where(value => !string.IsNullOrWhiteSpace(value)));

        // A new message rather than ReplyToItem, so a Reply-To header in the (untrusted) email can never pick the
        // recipient. The child order follows the EWS schema sequence: Subject, Body, InReplyTo, ToRecipients, From, References.
        var message = new XElement(
            T + "Message",
            new XElement(T + "Subject", subject),
            new XElement(T + "Body", new XAttribute("BodyType", "Text"), bodyText),
            original.InternetMessageId is null ? null : new XElement(T + "InReplyTo", original.InternetMessageId),
            new XElement(T + "ToRecipients", MailboxElement(original.From)),
            new XElement(T + "From", MailboxElement(new EmailAddress(_options.Mailbox, "SMTP"))),
            references.Length == 0 ? null : new XElement(T + "References", references));

        var request = new XElement(
            M + "CreateItem",
            new XAttribute("MessageDisposition", "SendAndSaveCopy"),
            new XElement(M + "SavedItemFolderId", EwsRequestBuilder.DistinguishedFolder("sentitems", _options.Mailbox)),
            new XElement(M + "Items", message));

        await InvokeSingleAsync("CreateItem", request, cancellationToken);
    }

    public async Task MoveAsync(IReadOnlyList<string> itemIds, string folderName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        if (itemIds.Count == 0)
        {
            return;
        }

        var folderId = await GetTopLevelFolderIdAsync(folderName, cancellationToken);
        var request = new XElement(
            M + "MoveItem",
            new XElement(M + "ToFolderId", new XElement(T + "FolderId", new XAttribute("Id", folderId))),
            new XElement(M + "ItemIds", itemIds.Select(id => new XElement(T + "ItemId", new XAttribute("Id", id)))));

        try
        {
            await InvokeAsync("MoveItem", request, cancellationToken);
        }
        catch (EwsOperationException)
        {
            // The folder may have been deleted or recreated; look it up again next time.
            _folderIds.TryRemove(folderName, out _);
            throw;
        }
    }

    private async Task<string> GetTopLevelFolderIdAsync(string folderName, CancellationToken cancellationToken)
    {
        if (_folderIds.TryGetValue(folderName, out var cached))
        {
            return cached;
        }

        var request = new XElement(
            M + "FindFolder",
            new XAttribute("Traversal", "Shallow"),
            new XElement(
                M + "FolderShape",
                new XElement(T + "BaseShape", "IdOnly"),
                new XElement(T + "AdditionalProperties", Field("folder:DisplayName"))),
            new XElement(M + "ParentFolderIds", EwsRequestBuilder.DistinguishedFolder("msgfolderroot", _options.Mailbox)));

        var response = await InvokeSingleAsync("FindFolder", request, cancellationToken);
        var folderId = (response.Element(M + "RootFolder")?.Element(T + "Folders")?.Elements() ?? [])
            .Where(folder => string.Equals(Text(folder, "DisplayName"), folderName, StringComparison.OrdinalIgnoreCase))
            .Select(folder => (string?)folder.Element(T + "FolderId")?.Attribute("Id"))
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));

        if (folderId is null)
        {
            throw new EwsOperationException(
                "FindFolder",
                FailureCategory.EwsError,
                $"The mailbox has no top-level folder named '{folderName}'; create it beside the Inbox.",
                "ErrorFolderNotFound");
        }

        _folderIds[folderName] = folderId;
        return folderId;
    }

    private async Task<XElement> InvokeSingleAsync(string operation, XElement request, CancellationToken cancellationToken) =>
        (await InvokeAsync(operation, request, cancellationToken))[0];

    /// <summary>Sends one operation and returns its response messages, or throws <see cref="EwsOperationException"/>.</summary>
    private async Task<IReadOnlyList<XElement>> InvokeAsync(string operation, XElement request, CancellationToken cancellationToken)
    {
        // Same defense in depth as the probe: never negotiate Windows authentication over plain HTTP.
        if (!Uri.TryCreate(_options.EwsUrl, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new EwsOperationException(operation, FailureCategory.Configuration, "Exchange:EwsUrl must be an absolute HTTPS URL. No request was sent.");
        }

        _logger.LogDebug("Sending EWS {Operation}.", operation);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(EwsRequestBuilder.BuildRequest(request), Encoding.UTF8, "text/xml"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EwsOperationException(
                operation,
                FailureCategory.Timeout,
                $"No complete response was received within {_httpClient.Timeout.TotalSeconds:0.##} seconds.",
                isTransient: true,
                mayHaveTakenEffect: true);
        }
        catch (HttpRequestException exception)
        {
            throw TransportFailure(operation, exception);
        }

        using (response)
        {
            return await InterpretAsync(operation, response, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<XElement>> InterpretAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new EwsOperationException(operation, FailureCategory.Authentication, "HTTP 401: Exchange did not accept the Windows credentials of this process.");
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new EwsOperationException(operation, FailureCategory.HttpAccessDenied, "HTTP 403: the request was refused before mailbox permissions were evaluated.");
        }

        if (status is >= 300 and < 400)
        {
            throw new EwsOperationException(operation, FailureCategory.UnexpectedRedirect, $"HTTP {status}: redirects are not followed so credentials are never forwarded.");
        }

        XElement body;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            body = EwsResponseParser.LoadEnvelope(text).Element(Soap + "Body")
                ?? throw new EwsProtocolException("The SOAP envelope has no Body element.");
        }
        catch (EwsProtocolException exception)
        {
            // An unreadable answer to a request that reached the server: it may or may not have been carried out.
            var serverError = status >= 500;
            throw new EwsOperationException(
                operation,
                response.IsSuccessStatusCode ? FailureCategory.SoapProtocol : FailureCategory.UnexpectedHttpStatus,
                $"HTTP {status}: {exception.Message}",
                isTransient: serverError,
                mayHaveTakenEffect: response.IsSuccessStatusCode || serverError);
        }

        if (body.Element(Soap + "Fault") is { } faultElement)
        {
            var fault = EwsResponseParser.ReadFault(faultElement);
            var codeText = fault.ResponseCode is null ? string.Empty : $" ({fault.ResponseCode})";
            throw new EwsOperationException(
                operation,
                EwsClient.CategoryFor(fault.ResponseCode),
                $"EWS returned a SOAP fault{codeText}: {fault.Message ?? fault.FaultString}",
                fault.ResponseCode,
                isTransient: IsTransientCode(fault.ResponseCode) || response.StatusCode == HttpStatusCode.ServiceUnavailable);
        }

        var messages = body.Element(M + (operation + "Response"))?.Element(M + "ResponseMessages")?.Elements().ToList();
        if (messages is not { Count: > 0 })
        {
            throw new EwsOperationException(
                operation,
                FailureCategory.SoapProtocol,
                $"HTTP {status}: the response contains no {operation} response message.",
                mayHaveTakenEffect: true);
        }

        var error = messages.FirstOrDefault(message => (string?)message.Attribute("ResponseClass") == "Error");
        if (error is not null)
        {
            var code = NullIfBlank((string?)error.Element(M + "ResponseCode"));
            throw new EwsOperationException(
                operation,
                EwsClient.CategoryFor(code),
                $"EWS {operation} returned {code ?? "an error"}: {(string?)error.Element(M + "MessageText")}",
                code,
                isTransient: IsTransientCode(code));
        }

        return messages;
    }

    private static EwsOperationException TransportFailure(string operation, HttpRequestException exception)
    {
        var detail = EwsClient.Describe(exception);

        if (EwsClient.IsTlsFailure(exception))
        {
            return new EwsOperationException(operation, FailureCategory.TlsFailure, $"TLS negotiation or certificate validation failed ({detail}).");
        }

        if (exception.HttpRequestError == HttpRequestError.UserAuthenticationError)
        {
            return new EwsOperationException(operation, FailureCategory.Authentication, $"The Windows credentials could not be used ({detail}).");
        }

        // Failures while connecting happen before the request is sent; anything later may have reached Exchange.
        var notSent = exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.ProxyTunnelError;

        return new EwsOperationException(
            operation,
            EwsClient.IsSocketTimeout(exception) ? FailureCategory.Timeout : FailureCategory.Connectivity,
            $"The request to Exchange failed ({detail}).",
            isTransient: true,
            mayHaveTakenEffect: !notSent);
    }

    private static bool IsTransientCode(string? code) => code is not null && TransientCodes.Contains(code);

    private static EwsOperationException Protocol(string operation, string message) =>
        new(operation, FailureCategory.SoapProtocol, message);

    private static XElement Field(string fieldUri) => new(T + "FieldURI", new XAttribute("FieldURI", fieldUri));

    private static XElement PageView(int maxEntries, int offset) => new(
        M + "IndexedPageItemView",
        new XAttribute("MaxEntriesReturned", maxEntries),
        new XAttribute("Offset", offset),
        new XAttribute("BasePoint", "Beginning"));

    private static XElement MailboxElement(EmailAddress address) => new(
        T + "Mailbox",
        new XElement(T + "EmailAddress", address.Address),
        string.IsNullOrWhiteSpace(address.RoutingType) ? null : new XElement(T + "RoutingType", address.RoutingType));

    private static string? Text(XElement parent, string localName) => NullIfBlank((string?)parent.Element(T + localName));

    private static DateTimeOffset ReadDate(XElement item) =>
        ((DateTimeOffset?)item.Element(T + "DateTimeReceived"))?.ToUniversalTime() ?? DateTimeOffset.MinValue;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
