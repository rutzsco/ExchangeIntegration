using System.Text.Json;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class InvoiceIngestionTests
{
    private const string Mailbox = "ap-invoices@contoso.com";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeMailbox _mailbox = new();
    private readonly InMemoryInvoiceStore _store = new();
    private readonly RecordingEngineQueues _queues = new();
    private readonly CapturingLogger<InvoiceIngestion> _logger = new();

    private InvoiceIngestion Create(int maxMessagesPerPoll = 20) => new(
        _mailbox,
        _store,
        _queues,
        Options.Create(new ExchangeOptions { Mailbox = Mailbox, MaxMessagesPerPoll = maxMessagesPerPoll }),
        new FixedTimeProvider(Now),
        _logger);

    [Fact]
    public async Task A_new_email_is_stored_announced_and_checkpointed_in_that_order()
    {
        _mailbox.AddEmail(
            "<inv-1001@example.test>",
            files: [("Invoice 1001.pdf", "application/pdf", "%PDF"u8.ToArray()), ("logo.png", null, [1, 2, 3])]);
        var key = MessageKeys.Compute(Mailbox, "<inv-1001@example.test>");

        var summary = await Create().PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(1, 1, 0, 0, 0), summary);

        var pdf = _store.Blobs[$"{key}/attachments/01-Invoice_1001.pdf"];
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal("%PDF"u8.ToArray(), pdf.Content.ToArray());
        Assert.Equal("application/octet-stream", _store.Blobs[$"{key}/attachments/02-logo.png"].ContentType);

        var manifest = _store.Blobs[BlobPaths.Manifest(key)].Content.ToObjectFromJson<InvoiceManifest>(AdapterJson.Options)!;
        Assert.Equal(key, manifest.MessageKey);
        Assert.Equal(Mailbox, manifest.Mailbox);
        Assert.Equal("<inv-1001@example.test>", manifest.InternetMessageId);
        Assert.Equal("supplier@example.test", manifest.From);
        Assert.Equal(Now, manifest.CapturedUtc);
        Assert.Equal(["Invoice 1001.pdf", "logo.png"], manifest.Files.Select(file => file.Name));
        Assert.Equal(4, manifest.Files[0].Size);

        var announced = Assert.Single(_queues.InvoicesReceived);
        Assert.Equal(MessageTypes.InvoiceReceived, announced.Type);
        Assert.Equal(key, announced.MessageKey);
        Assert.Equal("<inv-1001@example.test>", announced.InternetMessageId);
        Assert.Equal(new Uri(InMemoryInvoiceStore.ContainerUri, BlobPaths.Manifest(key)), announced.Manifest);
        Assert.Contains(key, _store.Announced);
    }

    [Fact]
    public async Task An_announced_email_is_not_captured_again()
    {
        _mailbox.AddEmail("<inv-1001@example.test>");
        var ingestion = Create();

        await ingestion.PollOnceAsync(CancellationToken.None);
        var second = await ingestion.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(1, 0, 1, 0, 0), second);
        Assert.Single(_queues.InvoicesReceived);
        Assert.Equal(1, _mailbox.GetMessageCalls);
        Assert.Equal(1, _store.IsAnnouncedCalls);
    }

    [Fact]
    public async Task Mailboxes_have_independent_capture_limits_keys_and_checkpoints_in_shared_storage()
    {
        const string otherAddress = "invoices-other@contoso.com";
        const string messageId = "<inv-1001@example.test>";
        var otherMailbox = new FakeMailbox();
        foreach (var mailbox in new[] { _mailbox, otherMailbox })
        {
            mailbox.AddEmail(messageId);
            mailbox.AddEmail("<inv-1002@example.test>");
        }
        var first = Create(maxMessagesPerPoll: 1);
        var second = new InvoiceIngestion(
            otherMailbox, _store, _queues,
            Options.Create(new ExchangeOptions { Mailbox = otherAddress, MaxMessagesPerPoll = 2 }),
            new FixedTimeProvider(Now), _logger);

        Assert.Equal(new PollSummary(2, 1, 0, 0, 1), await first.PollOnceAsync(CancellationToken.None));
        Assert.Equal(new PollSummary(2, 2, 0, 0, 0), await second.PollOnceAsync(CancellationToken.None));
        Assert.Equal(new PollSummary(2, 0, 2, 0, 0), await second.PollOnceAsync(CancellationToken.None));

        var events = _queues.InvoicesReceived.Where(message => message.InternetMessageId == messageId).ToArray();
        Assert.Equal(2, events.Length);
        Assert.NotEqual(events[0].MessageKey, events[1].MessageKey);
        Assert.NotEqual(events[0].Manifest, events[1].Manifest);
        foreach (var message in events)
        {
            var manifest = _store.Blobs[BlobPaths.Manifest(message.MessageKey)].Content.ToObjectFromJson<InvoiceManifest>(AdapterJson.Options)!;
            Assert.Equal(message.Mailbox, manifest.Mailbox);
            Assert.Contains(message.MessageKey, _store.Announced);
        }
        Assert.Equal(1, _mailbox.GetMessageCalls);
        Assert.Equal(2, otherMailbox.GetMessageCalls);
    }

    [Fact]
    public async Task After_a_restart_the_checkpoint_in_storage_prevents_a_second_capture()
    {
        _mailbox.AddEmail("<inv-1001@example.test>");
        await Create().PollOnceAsync(CancellationToken.None);

        var summary = await Create().PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(1, 0, 1, 0, 0), summary);
        Assert.Single(_queues.InvoicesReceived);
    }

    [Fact]
    public async Task A_failure_before_the_checkpoint_repeats_the_capture_next_poll()
    {
        _mailbox.AddEmail("<inv-1001@example.test>");
        var ingestion = Create();
        _queues.PublishInvoiceFailure = new InvalidOperationException("Service Bus unavailable");

        var failed = await ingestion.PollOnceAsync(CancellationToken.None);
        Assert.Equal(1, failed.Failed);
        Assert.Empty(_store.Announced);

        _queues.PublishInvoiceFailure = null;
        var retried = await ingestion.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, retried.Captured);
        Assert.Single(_queues.InvoicesReceived);
        Assert.Single(_store.Announced);
    }

    [Fact]
    public async Task One_failing_email_does_not_block_the_others()
    {
        _mailbox.AddEmail("<bad@example.test>", subject: "Secret subject");
        _mailbox.AddEmail("<good@example.test>");
        _mailbox.GetMessageFailures["<bad@example.test>"] = new EwsOperationException("GetItem", FailureCategory.EwsError, "boom", "ErrorItemCorrupt");

        var summary = await Create().PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(2, 1, 0, 1, 0), summary);
        Assert.Equal(MessageKeys.Compute(Mailbox, "<good@example.test>"), Assert.Single(_queues.InvoicesReceived).MessageKey);
        var error = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("ErrorItemCorrupt", error.Message);
        Assert.Contains(MessageKeys.Compute(Mailbox, "<bad@example.test>"), error.Message);
    }

    [Fact]
    public async Task Only_mail_with_a_message_id_is_captured_and_item_attachments_are_counted_as_skipped()
    {
        var email = _mailbox.AddEmail("<inv-1001@example.test>", files: [("a.pdf", "application/pdf", [1])]);
        _mailbox.AddItemAttachment(email.ItemId, "Forwarded");
        _mailbox.AddEmail("<meeting@example.test>", itemClass: "IPM.Schedule.Meeting.Request");
        _mailbox.AddInboxItemWithoutMessageId();

        var summary = await Create().PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Captured);
        var manifest = _store.Blobs[BlobPaths.Manifest(MessageKeys.Compute(Mailbox, "<inv-1001@example.test>"))]
            .Content.ToObjectFromJson<InvoiceManifest>(AdapterJson.Options)!;
        Assert.Single(manifest.Files);
        Assert.Equal(1, manifest.SkippedAttachments);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("no Message-ID"));
    }

    [Fact]
    public async Task At_most_MaxMessagesPerPoll_emails_are_captured_per_poll_oldest_first()
    {
        _mailbox.AddEmail("<1@example.test>");
        _mailbox.AddEmail("<2@example.test>");
        _mailbox.AddEmail("<3@example.test>");
        var ingestion = Create(maxMessagesPerPoll: 2);

        var first = await ingestion.PollOnceAsync(CancellationToken.None);
        var second = await ingestion.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(3, 2, 0, 0, 1), first);
        Assert.Equal(new PollSummary(3, 1, 2, 0, 0), second);
        Assert.Equal(
            ["<1@example.test>", "<2@example.test>", "<3@example.test>"],
            _queues.InvoicesReceived.Select(message => message.InternetMessageId));
    }

    [Fact]
    public async Task Emails_that_keep_failing_do_not_use_up_the_per_poll_cap()
    {
        _mailbox.AddEmail("<bad-1@example.test>");
        _mailbox.AddEmail("<bad-2@example.test>");
        _mailbox.AddEmail("<good@example.test>");
        foreach (var id in new[] { "<bad-1@example.test>", "<bad-2@example.test>" })
        {
            _mailbox.GetMessageFailures[id] = new EwsOperationException("GetItem", FailureCategory.EwsError, "boom", "ErrorItemCorrupt");
        }

        var summary = await Create(maxMessagesPerPoll: 1).PollOnceAsync(CancellationToken.None);

        Assert.Equal(new PollSummary(3, 1, 0, 2, 0), summary);
        Assert.Equal("<good@example.test>", Assert.Single(_queues.InvoicesReceived).InternetMessageId);
    }

    [Fact]
    public async Task Logs_never_contain_the_subject_or_sender()
    {
        _mailbox.AddEmail("<inv-1001@example.test>", from: "private.person@example.test", subject: "Confidential subject");

        await Create().PollOnceAsync(CancellationToken.None);

        Assert.DoesNotContain("private.person", _logger.Everything());
        Assert.DoesNotContain("Confidential subject", _logger.Everything());
    }

    [Fact]
    public void The_invoice_received_message_serializes_to_the_documented_contract()
    {
        var json = JsonSerializer.Serialize(
            new InvoiceReceivedMessage
            {
                MessageKey = "3f9a0c1d2e3f4a5b6c7d8e9f0a1b2c62",
                Mailbox = Mailbox,
                InternetMessageId = "<inv-1001@example.test>",
                ReceivedUtc = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
                Manifest = new Uri("https://contosoinvoices.blob.core.windows.net/invoice-intake/3f9a0c1d2e3f4a5b6c7d8e9f0a1b2c62/manifest.json"),
            },
            AdapterJson.Options);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["type", "messageKey", "mailbox", "internetMessageId", "receivedUtc", "manifest"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("invoice.received", document.RootElement.GetProperty("type").GetString());
    }
}
