using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class ExchangeCommandHandlerTests
{
    private const string Mailbox = "ap-invoices@contoso.com";
    private const string MessageId = "<inv-1001@example.test>";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Key = MessageKeys.Compute(Mailbox, MessageId);

    private readonly FakeMailbox _mailbox = new();
    private readonly InMemoryInvoiceStore _store = new();
    private readonly RecordingEngineQueues _queues = new();

    private ExchangeCommandHandler Create() => new(
        _mailbox,
        _store,
        _queues,
        Options.Create(new ExchangeOptions { Mailbox = Mailbox, MoveFolders = ["Processed", "Rejected"] }),
        new FixedTimeProvider(Now),
        new CapturingLogger<ExchangeCommandHandler>());

    private static ExchangeCommand Reply(string body = "Thanks, we received your invoice.") => new()
    {
        Type = MessageTypes.Reply,
        CommandId = $"{Key}.reply",
        MessageKey = Key,
        InternetMessageId = MessageId,
        Mailbox = Mailbox,
        Reply = new ReplyDetails { BodyText = body },
    };

    private static ExchangeCommand Move(string folder = "Processed") => new()
    {
        Type = MessageTypes.Move,
        CommandId = $"{Key}.move",
        MessageKey = Key,
        InternetMessageId = MessageId,
        Mailbox = Mailbox,
        Move = new MoveDetails { Folder = folder },
    };

    private Task<CommandOutcome> Handle(ExchangeCommand command) =>
        Create().HandleAsync(BinaryData.FromObjectAsJson(command, AdapterJson.Options), CancellationToken.None);

    private CommandRecord? Record(string commandId) =>
        _store.Blobs.TryGetValue(BlobPaths.CommandRecord(Key, commandId), out var blob)
            ? blob.Content.ToObjectFromJson<CommandRecord>(AdapterJson.Options)
            : null;

    [Fact]
    public async Task A_move_moves_the_email_and_publishes_success()
    {
        _mailbox.AddEmail(MessageId);

        var outcome = await Handle(Move("processed"));

        var move = Assert.Single(_mailbox.Moves);
        Assert.Equal(["item-1"], move.ItemIds);
        Assert.Equal("Processed", move.Folder);

        var result = Assert.Single(_queues.Results);
        Assert.Same(result, outcome.Result);
        Assert.Null(outcome.DeadLetterReason);
        Assert.Equal(MessageTypes.CommandResult, result.Type);
        Assert.Equal($"{Key}.move", result.CommandId);
        Assert.Equal(Key, result.MessageKey);
        Assert.Equal(CommandResultMessage.Succeeded, result.Status);
        Assert.Equal(Now, result.CompletedUtc);
        Assert.Equal(CommandRecord.Succeeded, Record($"{Key}.move")!.Status);
    }

    [Fact]
    public async Task A_move_leaves_a_second_inbox_item_with_the_same_message_id_alone()
    {
        _mailbox.AddEmail(MessageId);
        _mailbox.AddEmail(MessageId);

        await Handle(Move());

        Assert.Equal(["item-1"], Assert.Single(_mailbox.Moves).ItemIds);
    }

    [Fact]
    public async Task A_reply_is_sent_to_the_original_email_and_recorded()
    {
        var email = _mailbox.AddEmail(MessageId);

        await Handle(Reply());

        var sent = Assert.Single(_mailbox.SentReplies);
        Assert.Equal(email, sent.Original);
        Assert.Equal("Thanks, we received your invoice.", sent.BodyText);
        Assert.Equal(CommandResultMessage.Succeeded, Assert.Single(_queues.Results).Status);
        Assert.Equal(CommandRecord.Succeeded, Record($"{Key}.reply")!.Status);
    }

    [Fact]
    public async Task A_repeated_command_republishes_the_stored_result_without_acting_again()
    {
        _mailbox.AddEmail(MessageId);
        await Handle(Reply());

        await Handle(Reply());

        Assert.Single(_mailbox.SentReplies);
        Assert.Equal(2, _queues.Results.Count);
        Assert.Equal(_queues.Results[0].CompletedUtc, _queues.Results[1].CompletedUtc);
        Assert.Equal(CommandResultMessage.Succeeded, _queues.Results[1].Status);
    }

    [Fact]
    public async Task A_reply_whose_outcome_is_unknown_is_never_sent_twice()
    {
        _mailbox.AddEmail(MessageId);
        _mailbox.SendReplyFailure = new EwsOperationException("CreateItem", FailureCategory.Timeout, "No answer", isTransient: true, mayHaveTakenEffect: true);

        await Handle(Reply());
        _mailbox.SendReplyFailure = null;
        await Handle(Reply());

        Assert.Empty(_mailbox.SentReplies);
        Assert.All(_queues.Results, result =>
        {
            Assert.Equal(CommandResultMessage.Failed, result.Status);
            Assert.False(result.Retryable);
            Assert.Contains("Sent Items", result.Reason);
        });
        Assert.Equal(CommandRecord.Started, Record($"{Key}.reply")!.Status);
    }

    [Fact]
    public async Task A_reply_refused_by_exchange_can_be_retried()
    {
        _mailbox.AddEmail(MessageId);
        _mailbox.SendReplyFailure = new EwsOperationException("CreateItem", FailureCategory.EwsError, "Busy", "ErrorServerBusy", isTransient: true);

        await Handle(Reply());

        var result = Assert.Single(_queues.Results);
        Assert.Equal(CommandResultMessage.Failed, result.Status);
        Assert.True(result.Retryable);
        Assert.Contains("ErrorServerBusy", result.Reason);
        Assert.Null(Record($"{Key}.reply"));

        _mailbox.SendReplyFailure = null;
        await Handle(Reply());
        Assert.Single(_mailbox.SentReplies);
    }

    [Fact]
    public async Task A_failed_move_reports_whether_it_is_retryable()
    {
        _mailbox.AddEmail(MessageId);
        _mailbox.MoveFailure = new EwsOperationException("FindFolder", FailureCategory.EwsError, "No folder", "ErrorFolderNotFound");

        await Handle(Move());

        var result = Assert.Single(_queues.Results);
        Assert.Equal(CommandResultMessage.Failed, result.Status);
        Assert.False(result.Retryable);
        Assert.Contains("ErrorFolderNotFound", result.Reason);
    }

    [Fact]
    public async Task A_command_for_an_email_no_longer_in_the_inbox_fails()
    {
        var result = (await Handle(Move())).Result!;

        Assert.Equal(CommandResultMessage.Failed, result.Status);
        Assert.False(result.Retryable);
        Assert.Contains("not in the Inbox", result.Reason);
    }

    public static TheoryData<string, ExchangeCommand> InvalidCommands() => new()
    {
        { "Unknown command type", new ExchangeCommand { Type = "exchange.command.delete", CommandId = $"{Key}.x", MessageKey = Key, InternetMessageId = MessageId, Mailbox = Mailbox } },
        { "different mailbox", WithMailbox(Reply(), "someone-else@contoso.com") },
        { "internetMessageId is required", new ExchangeCommand { Type = MessageTypes.Reply, CommandId = $"{Key}.reply", MessageKey = Key, Mailbox = Mailbox, Reply = new ReplyDetails { BodyText = "x" } } },
        { "messageKey does not match", new ExchangeCommand { Type = MessageTypes.Reply, CommandId = $"{Key}.reply", MessageKey = Key, InternetMessageId = "<other@example.test>", Mailbox = Mailbox, Reply = new ReplyDetails { BodyText = "x" } } },
        { "commandId must start with the messageKey", new ExchangeCommand { Type = MessageTypes.Reply, CommandId = $"{new string('0', 32)}.reply", MessageKey = Key, InternetMessageId = MessageId, Mailbox = Mailbox, Reply = new ReplyDetails { BodyText = "x" } } },
        { "reply.bodyText is required", Reply("   ") },
        { "longer than", Reply(new string('a', ExchangeCommandHandler.MaxReplyLength + 1)) },
        { "not in Exchange:MoveFolders", Move("Deleted Items") },
    };

    private static ExchangeCommand WithMailbox(ExchangeCommand command, string mailbox) => new()
    {
        Type = command.Type,
        CommandId = command.CommandId,
        MessageKey = command.MessageKey,
        InternetMessageId = command.InternetMessageId,
        Mailbox = mailbox,
        Reply = command.Reply,
    };

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public async Task An_invalid_command_fails_without_touching_the_mailbox(string expectedReason, ExchangeCommand command)
    {
        _mailbox.AddEmail(MessageId);

        await Handle(command);

        var result = Assert.Single(_queues.Results);
        Assert.Equal(CommandResultMessage.Failed, result.Status);
        Assert.False(result.Retryable);
        Assert.Contains(expectedReason, result.Reason);
        Assert.Equal(command.CommandId, result.CommandId);
        Assert.Empty(_mailbox.SentReplies);
        Assert.Empty(_mailbox.Moves);
        Assert.Equal(0, _mailbox.GetMessageCalls);
    }

    [Theory]
    [InlineData("supplier@example.test", null, true)]
    [InlineData("supplier@example.test", "no", true)]
    [InlineData("supplier@example.test", "auto-replied", false)]
    [InlineData("AP-Invoices@Contoso.com", null, false)]
    public async Task Replies_are_refused_to_automatic_mail_and_to_the_mailbox_itself(string from, string? autoSubmitted, bool expectSent)
    {
        _mailbox.AddEmail(MessageId, from: from, autoSubmitted: autoSubmitted);

        await Handle(Reply());

        Assert.Equal(expectSent ? 1 : 0, _mailbox.SentReplies.Count);
        Assert.Equal(expectSent ? CommandResultMessage.Succeeded : CommandResultMessage.Failed, Assert.Single(_queues.Results).Status);
    }

    [Fact]
    public async Task The_mailbox_in_a_command_is_compared_case_insensitively()
    {
        _mailbox.AddEmail(MessageId);

        await Handle(WithMailbox(Reply(), "AP-Invoices@Contoso.com"));

        Assert.Single(_mailbox.SentReplies);
    }

    [Theory]
    [InlineData("not json", "InvalidJson")]
    [InlineData("""{"type":"exchange.command.reply"}""", "InvalidCommandId")]
    [InlineData("""{"type":"exchange.command.reply","commandId":"reply-1"}""", "InvalidCommandId")]
    [InlineData("null", "InvalidCommandId")]
    public async Task A_message_without_a_usable_commandId_is_dead_lettered(string body, string reason)
    {
        var outcome = await Create().HandleAsync(BinaryData.FromString(body), CancellationToken.None);

        Assert.Equal(reason, outcome.DeadLetterReason);
        Assert.Null(outcome.Result);
        Assert.Empty(_queues.Results);
    }
}
