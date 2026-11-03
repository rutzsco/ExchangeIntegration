using System.Net;
using System.Xml.Linq;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class EwsMailboxTests
{
    private const string Mailbox = "ap-invoices@contoso.com";
    private static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
    private static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";

    private readonly CapturingLogger<EwsMailbox> _logger = new();

    private EwsMailbox Create(StubHttpMessageHandler handler, string ewsUrl = "https://mail.contoso.com/EWS/Exchange.asmx") =>
        new(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
            Options.Create(new ExchangeOptions { EwsUrl = ewsUrl, Mailbox = Mailbox }),
            _logger);

    private static StubHttpMessageHandler Sequence(params string[] bodies)
    {
        var index = 0;
        return new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, bodies[Math.Min(index++, bodies.Length - 1)])));
    }

    private static XElement Operation(CapturedRequest request) =>
        XDocument.Parse(request.Body).Root!.Element(XName.Get("Body", "http://schemas.xmlsoap.org/soap/envelope/"))!.Elements().Single();

    [Fact]
    public async Task ListInbox_pages_through_the_inbox_oldest_first()
    {
        var handler = Sequence(
            EwsSamples.FindItem(false, 2, ("item-1", "IPM.Note", "<a@example.test>"), ("item-2", "IPM.Schedule.Meeting.Request", "<b@example.test>")),
            EwsSamples.FindItem(true, 3, ("item-3", "IPM.Note", null)));

        var items = await Create(handler).ListInboxAsync(CancellationToken.None);

        Assert.Equal(["item-1", "item-2", "item-3"], items.Select(item => item.ItemId));
        Assert.Equal("<a@example.test>", items[0].InternetMessageId);
        Assert.Equal("IPM.Schedule.Meeting.Request", items[1].ItemClass);
        Assert.Null(items[2].InternetMessageId);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero), items[0].ReceivedUtc);

        Assert.Equal(2, handler.Requests.Count);
        var first = Operation(handler.Requests[0]);
        Assert.Equal("Shallow", (string?)first.Attribute("Traversal"));
        Assert.Equal("0", (string?)first.Element(M + "IndexedPageItemView")!.Attribute("Offset"));
        Assert.Equal("Ascending", (string?)first.Element(M + "SortOrder")!.Element(T + "FieldOrder")!.Attribute("Order"));
        var inbox = first.Element(M + "ParentFolderIds")!.Element(T + "DistinguishedFolderId")!;
        Assert.Equal("inbox", (string?)inbox.Attribute("Id"));
        Assert.Equal(Mailbox, (string?)inbox.Element(T + "Mailbox")!.Element(T + "EmailAddress"));
        Assert.Equal("2", (string?)Operation(handler.Requests[1]).Element(M + "IndexedPageItemView")!.Attribute("Offset"));
    }

    [Fact]
    public async Task GetMessage_reads_sender_threading_headers_and_attachments()
    {
        var mailbox = Create(Sequence(EwsSamples.GetItem));

        var message = await mailbox.GetMessageAsync("item-1", CancellationToken.None);

        Assert.Equal("<inv-1001@example.test>", message.InternetMessageId);
        Assert.Equal("Invoice 1001", message.Subject);
        Assert.Equal(new EmailAddress("supplier@example.test", "SMTP"), message.From);
        Assert.Equal("<earlier@example.test>", message.References);
        Assert.Equal("auto-replied", message.AutoSubmitted);
        Assert.Collection(
            message.Attachments,
            file =>
            {
                Assert.True(file.IsFile);
                Assert.Equal("att-1", file.AttachmentId);
                Assert.Equal("invoice 1001.pdf", file.Name);
                Assert.Equal("application/pdf", file.ContentType);
                Assert.Equal(1234, file.Size);
            },
            item => Assert.False(item.IsFile));

        Assert.DoesNotContain("supplier@example.test", _logger.Everything());
        Assert.DoesNotContain("Invoice 1001", _logger.Everything());
    }

    [Fact]
    public async Task GetAttachmentContent_decodes_the_base64_content()
    {
        var mailbox = Create(Sequence(EwsSamples.GetAttachment(Convert.ToBase64String("%PDF-1.7"u8.ToArray()))));

        var content = await mailbox.GetAttachmentContentAsync("att-1", CancellationToken.None);

        Assert.Equal("%PDF-1.7"u8.ToArray(), content);
    }

    [Fact]
    public async Task GetAttachmentContent_rejects_content_that_is_not_base64()
    {
        var mailbox = Create(Sequence(EwsSamples.GetAttachment("not base64!")));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.GetAttachmentContentAsync("att-1", CancellationToken.None));

        Assert.Equal(FailureCategory.SoapProtocol, exception.Category);
    }

    [Fact]
    public async Task SendReply_sends_a_new_threaded_message_to_the_original_sender_from_the_mailbox()
    {
        var handler = Sequence(EwsSamples.CreateItem());
        var original = new EmailMessage(
            "item-1",
            "<inv-1001@example.test>",
            "Invoice 1001",
            new EmailAddress("supplier@example.test", "SMTP"),
            DateTimeOffset.UnixEpoch,
            "<earlier@example.test>",
            null,
            []);

        await Create(handler).SendReplyAsync(original, "Thanks, we received your invoice.", CancellationToken.None);

        var create = Operation(Assert.Single(handler.Requests));
        Assert.Equal("CreateItem", create.Name.LocalName);
        Assert.Equal("SendAndSaveCopy", (string?)create.Attribute("MessageDisposition"));
        Assert.Equal("sentitems", (string?)create.Element(M + "SavedItemFolderId")!.Element(T + "DistinguishedFolderId")!.Attribute("Id"));

        var message = create.Element(M + "Items")!.Element(T + "Message")!;
        Assert.Equal(
            ["Subject", "Body", "InReplyTo", "ToRecipients", "From", "References"],
            message.Elements().Select(element => element.Name.LocalName));
        Assert.Equal("RE: Invoice 1001", (string?)message.Element(T + "Subject"));
        Assert.Equal("Text", (string?)message.Element(T + "Body")!.Attribute("BodyType"));
        Assert.Equal("Thanks, we received your invoice.", (string?)message.Element(T + "Body"));
        Assert.Equal("<inv-1001@example.test>", (string?)message.Element(T + "InReplyTo"));
        Assert.Equal("supplier@example.test", (string?)message.Element(T + "ToRecipients")!.Descendants(T + "EmailAddress").Single());
        Assert.Equal(Mailbox, (string?)message.Element(T + "From")!.Descendants(T + "EmailAddress").Single());
        Assert.Equal("<earlier@example.test> <inv-1001@example.test>", (string?)message.Element(T + "References"));
    }

    [Fact]
    public async Task SendReply_does_not_stack_reply_prefixes()
    {
        var handler = Sequence(EwsSamples.CreateItem());
        var original = new EmailMessage("item-1", null, "Re: Invoice 1001", new EmailAddress("supplier@example.test", null), DateTimeOffset.UnixEpoch, null, null, []);

        await Create(handler).SendReplyAsync(original, "Thanks.", CancellationToken.None);

        var message = Operation(handler.Requests[0]).Element(M + "Items")!.Element(T + "Message")!;
        Assert.Equal("Re: Invoice 1001", (string?)message.Element(T + "Subject"));
        Assert.Null(message.Element(T + "InReplyTo"));
        Assert.Null(message.Element(T + "References"));
    }

    [Fact]
    public async Task Move_finds_the_top_level_folder_once_and_moves_every_item()
    {
        var handler = Sequence(
            EwsSamples.FindFolder(("folder-inbox", "Inbox"), ("folder-processed", "Processed")),
            EwsSamples.MoveItem(),
            EwsSamples.MoveItem());
        var mailbox = Create(handler);

        await mailbox.MoveAsync(["item-1", "item-2"], "processed", CancellationToken.None);
        await mailbox.MoveAsync(["item-3"], "Processed", CancellationToken.None);

        Assert.Equal(["FindFolder", "MoveItem", "MoveItem"], handler.Requests.Select(request => Operation(request).Name.LocalName));
        var findFolder = Operation(handler.Requests[0]);
        Assert.Equal("msgfolderroot", (string?)findFolder.Element(M + "ParentFolderIds")!.Element(T + "DistinguishedFolderId")!.Attribute("Id"));
        var move = Operation(handler.Requests[1]);
        Assert.Equal("folder-processed", (string?)move.Element(M + "ToFolderId")!.Element(T + "FolderId")!.Attribute("Id"));
        Assert.Equal(["item-1", "item-2"], move.Element(M + "ItemIds")!.Elements().Select(item => (string?)item.Attribute("Id")));
    }

    [Fact]
    public async Task Move_to_a_missing_folder_fails_without_moving_anything()
    {
        var handler = Sequence(EwsSamples.FindFolder(("folder-inbox", "Inbox")));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => Create(handler).MoveAsync(["item-1"], "Processed", CancellationToken.None));

        Assert.Equal("ErrorFolderNotFound", exception.ResponseCode);
        Assert.False(exception.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("ErrorServerBusy", true, FailureCategory.EwsError)]
    [InlineData("ErrorAccessDenied", false, FailureCategory.MailboxAuthorization)]
    [InlineData("ErrorItemNotFound", false, FailureCategory.EwsError)]
    public async Task An_error_response_is_classified_and_known_not_to_have_taken_effect(string code, bool transient, FailureCategory category)
    {
        var mailbox = Create(Sequence(EwsSamples.Error("CreateItem", code, "Something went wrong.")));
        var original = new EmailMessage("item-1", null, "x", new EmailAddress("supplier@example.test", null), DateTimeOffset.UnixEpoch, null, null, []);

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.SendReplyAsync(original, "Thanks.", CancellationToken.None));

        Assert.Equal("CreateItem", exception.Operation);
        Assert.Equal(code, exception.ResponseCode);
        Assert.Equal(category, exception.Category);
        Assert.Equal(transient, exception.IsTransient);
        Assert.False(exception.MayHaveTakenEffect);
    }

    [Fact]
    public async Task A_timeout_is_transient_and_may_have_taken_effect()
    {
        var mailbox = Create(StubHttpMessageHandler.Throwing(new TaskCanceledException("The request was canceled.")));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.ListInboxAsync(CancellationToken.None));

        Assert.Equal(FailureCategory.Timeout, exception.Category);
        Assert.True(exception.IsTransient);
        Assert.True(exception.MayHaveTakenEffect);
    }

    [Fact]
    public async Task A_connection_failure_is_transient_and_known_not_to_have_taken_effect()
    {
        var mailbox = Create(StubHttpMessageHandler.Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused")));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.ListInboxAsync(CancellationToken.None));

        Assert.Equal(FailureCategory.Connectivity, exception.Category);
        Assert.True(exception.IsTransient);
        Assert.False(exception.MayHaveTakenEffect);
    }

    [Fact]
    public async Task A_server_error_page_is_transient_and_may_have_taken_effect()
    {
        var mailbox = Create(StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "<html><body>Server Error</body></html>", "text/html"));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.ListInboxAsync(CancellationToken.None));

        Assert.Equal(FailureCategory.UnexpectedHttpStatus, exception.Category);
        Assert.True(exception.IsTransient);
        Assert.True(exception.MayHaveTakenEffect);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FailureCategory.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, FailureCategory.HttpAccessDenied)]
    [InlineData(HttpStatusCode.Found, FailureCategory.UnexpectedRedirect)]
    public async Task Http_refusals_are_classified(HttpStatusCode status, FailureCategory category)
    {
        var mailbox = Create(StubHttpMessageHandler.Returning(status));

        var exception = await Assert.ThrowsAsync<EwsOperationException>(() => mailbox.ListInboxAsync(CancellationToken.None));

        Assert.Equal(category, exception.Category);
        Assert.False(exception.MayHaveTakenEffect);
    }

    [Fact]
    public async Task A_plain_http_endpoint_is_refused_before_any_request()
    {
        var handler = Sequence(EwsSamples.CreateItem());

        var exception = await Assert.ThrowsAsync<EwsOperationException>(
            () => Create(handler, "http://mail.contoso.com/EWS/Exchange.asmx").ListInboxAsync(CancellationToken.None));

        Assert.Equal(FailureCategory.Configuration, exception.Category);
        Assert.Empty(handler.Requests);
    }
}
