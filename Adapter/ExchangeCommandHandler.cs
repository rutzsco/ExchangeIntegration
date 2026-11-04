using System.Text.Json;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Logging;

namespace ExchangeIntegration.Adapter;

/// <summary>The result of handling one command message: a result was published, or the message must be dead-lettered.</summary>
public sealed record CommandOutcome(CommandResultMessage? Result, string? DeadLetterReason = null, string? DeadLetterDescription = null)
{
    public static CommandOutcome DeadLetter(string reason, string description) => new(null, reason, description);
}

/// <summary>
/// Carries out one Engine command (reply or move) and publishes its result. Every command is checked against the
/// configured mailboxes and the target's allow-list first, because the queue is a trust boundary. Exceptions from
/// Blob Storage or Service Bus propagate so the message is abandoned and redelivered.
/// </summary>
public sealed class ExchangeCommandHandler
{
    public const int MaxReplyLength = 20_000;

    private readonly IReadOnlyDictionary<string, ConfiguredMailbox> _mailboxes;
    private readonly IInvoiceStore _store;
    private readonly IEngineQueues _queues;
    private readonly TimeProvider _time;
    private readonly ILogger<ExchangeCommandHandler> _logger;

    public ExchangeCommandHandler(
        IEnumerable<ConfiguredMailbox> mailboxes,
        IInvoiceStore store,
        IEngineQueues queues,
        TimeProvider time,
        ILogger<ExchangeCommandHandler> logger)
    {
        _mailboxes = mailboxes.ToDictionary(mailbox => mailbox.Options.Mailbox, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _queues = queues;
        _time = time;
        _logger = logger;
    }

    public async Task<CommandOutcome> HandleAsync(BinaryData body, CancellationToken cancellationToken)
    {
        ExchangeCommand? command;
        try
        {
            command = body.ToObjectFromJson<ExchangeCommand>(AdapterJson.Options);
        }
        catch (JsonException)
        {
            _logger.LogWarning("A command message is not valid JSON; dead-lettering it.");
            return CommandOutcome.DeadLetter("InvalidJson", "The message body is not a valid JSON command.");
        }

        if (command is null || !MessageKeys.IsWellFormedCommandId(command.CommandId))
        {
            _logger.LogWarning("A command message has no well-formed commandId; dead-lettering it.");
            return CommandOutcome.DeadLetter("InvalidCommandId", "commandId must be <messageKey>.<suffix>; no result can be correlated without it.");
        }

        var commandId = command.CommandId!;
        var outcome = await ExecuteAsync(command, commandId, cancellationToken);

        await _queues.PublishCommandResultAsync(outcome, cancellationToken);
        _logger.LogInformation(
            "Command {CommandId} ({Type}) {Status}{Reason}",
            commandId,
            command.Type,
            outcome.Status,
            outcome.Reason is null ? "." : $": {outcome.Reason}");
        return new CommandOutcome(outcome);
    }

    private async Task<CommandResultMessage> ExecuteAsync(ExchangeCommand command, string commandId, CancellationToken cancellationToken)
    {
        if (!_mailboxes.TryGetValue(command.Mailbox?.Trim() ?? string.Empty, out var mailbox))
        {
            return Failed(commandId, commandId[..32], "The command mailbox is not configured in this adapter.", retryable: false);
        }

        var invalid = Validate(command, commandId, mailbox, out var messageKey, out var folder);
        if (invalid is not null)
        {
            return Failed(commandId, messageKey, invalid, retryable: false);
        }

        var recordBlob = BlobPaths.CommandRecord(messageKey!, commandId);
        var record = await ReadRecordAsync(recordBlob, cancellationToken);
        if (record?.Status == CommandRecord.Succeeded && record.Result is not null)
        {
            return record.Result;
        }

        if (record?.Status == CommandRecord.Started)
        {
            return Failed(
                commandId,
                messageKey,
                "A previous attempt may have sent the reply; check Sent Items before issuing a new command.",
                retryable: false);
        }

        CommandResultMessage result;
        try
        {
            var itemIds = await mailbox.Client.FindInboxItemIdsAsync(command.InternetMessageId!, cancellationToken);
            if (itemIds.Count == 0)
            {
                return Failed(commandId, messageKey, "The email is not in the Inbox (already moved or deleted).", retryable: false);
            }

            if (itemIds.Count > 1)
            {
                _logger.LogWarning(
                    "{Count} Inbox items share the Message-ID of {MessageKey}; only the first is acted on, the rest stay in the Inbox.",
                    itemIds.Count,
                    messageKey);
            }

            if (command.Type == MessageTypes.Move)
            {
                await mailbox.Client.MoveAsync([itemIds[0]], folder!, cancellationToken);
            }
            else
            {
                var refusal = await ReplyAsync(mailbox, command, itemIds[0], recordBlob, cancellationToken);
                if (refusal is not null)
                {
                    return Failed(commandId, messageKey, refusal, retryable: false);
                }
            }

            result = new CommandResultMessage
            {
                CommandId = commandId,
                MessageKey = messageKey,
                Status = CommandResultMessage.Succeeded,
                CompletedUtc = _time.GetUtcNow(),
            };
        }
        catch (EwsOperationException exception)
        {
            return Failed(commandId, messageKey, InvoiceIngestion.Describe(exception), exception.IsTransient);
        }

        await WriteRecordAsync(recordBlob, new CommandRecord { Status = CommandRecord.Succeeded, Result = result, UpdatedUtc = _time.GetUtcNow() }, cancellationToken);
        return result;
    }

    /// <summary>Sends the reply. Returns a refusal reason, or null once sent.</summary>
    private async Task<string?> ReplyAsync(ConfiguredMailbox mailbox, ExchangeCommand command, string itemId, string recordBlob, CancellationToken cancellationToken)
    {
        var original = await mailbox.Client.GetMessageAsync(itemId, cancellationToken);

        if (original.From is null || string.IsNullOrWhiteSpace(original.From.Address))
        {
            return "The email has no sender address to reply to.";
        }

        if (_mailboxes.ContainsKey(original.From.Address.Trim()))
        {
            return "The email was sent by a monitored mailbox; replying would risk a mail loop.";
        }

        if (original.AutoSubmitted is { } autoSubmitted && !string.Equals(autoSubmitted, "no", StringComparison.OrdinalIgnoreCase))
        {
            return "The email is automatic (Auto-Submitted header); replying would risk a mail loop.";
        }

        // At most once: the started record stays if the outcome of the send is unknown, so a redelivery cannot send twice.
        await WriteRecordAsync(recordBlob, new CommandRecord { Status = CommandRecord.Started, UpdatedUtc = _time.GetUtcNow() }, cancellationToken);
        try
        {
            await mailbox.Client.SendReplyAsync(original, command.Reply!.BodyText!, CancellationToken.None);
        }
        catch (EwsOperationException exception) when (exception.MayHaveTakenEffect)
        {
            return $"The reply may or may not have been sent ({InvoiceIngestion.Describe(exception)}); check Sent Items before issuing a new command.";
        }
        catch (EwsOperationException)
        {
            await _store.DeleteAsync(recordBlob, cancellationToken);
            throw;
        }

        return null;
    }

    private static string? Validate(ExchangeCommand command, string commandId, ConfiguredMailbox mailbox, out string? messageKey, out string? folder)
    {
        // The well-formed commandId starts with a 32-hex key; echo that rather than an arbitrary messageKey string.
        messageKey = commandId[..32];
        folder = null;

        if (command.Type is not (MessageTypes.Reply or MessageTypes.Move))
        {
            return $"Unknown command type '{Truncate(command.Type)}'.";
        }

        if (string.IsNullOrWhiteSpace(command.InternetMessageId))
        {
            return "internetMessageId is required.";
        }

        var expectedKey = MessageKeys.Compute(mailbox.Options.Mailbox, command.InternetMessageId);
        if (!string.Equals(command.MessageKey, expectedKey, StringComparison.Ordinal))
        {
            return "messageKey does not match the mailbox and internetMessageId.";
        }

        if (!commandId.StartsWith(expectedKey + ".", StringComparison.Ordinal))
        {
            return "commandId must start with the messageKey.";
        }

        if (command.Type == MessageTypes.Reply)
        {
            var body = command.Reply?.BodyText;
            if (string.IsNullOrWhiteSpace(body))
            {
                return "reply.bodyText is required.";
            }

            if (body.Length > MaxReplyLength)
            {
                return $"reply.bodyText is longer than {MaxReplyLength} characters.";
            }

            return null;
        }

        var requested = command.Move?.Folder?.Trim();
        folder = mailbox.Options.GetMoveFolders().FirstOrDefault(allowed => string.Equals(allowed, requested, StringComparison.OrdinalIgnoreCase));
        return folder is null ? $"move.folder '{Truncate(requested)}' is not in the target mailbox's MoveFolders." : null;
    }

    private async Task<CommandRecord?> ReadRecordAsync(string blobName, CancellationToken cancellationToken)
    {
        var content = await _store.DownloadAsync(blobName, cancellationToken);
        return content?.ToObjectFromJson<CommandRecord>(AdapterJson.Options);
    }

    private Task WriteRecordAsync(string blobName, CommandRecord record, CancellationToken cancellationToken) =>
        _store.UploadAsync(blobName, BinaryData.FromObjectAsJson(record, AdapterJson.Options), "application/json", cancellationToken);

    private CommandResultMessage Failed(string commandId, string? messageKey, string reason, bool retryable) => new()
    {
        CommandId = commandId,
        MessageKey = messageKey,
        Status = CommandResultMessage.Failed,
        Reason = reason,
        Retryable = retryable,
        CompletedUtc = _time.GetUtcNow(),
    };

    private static string Truncate(string? value) =>
        value is null ? string.Empty : Redaction.SanitizeMessage(value.Length <= 64 ? value : value[..64] + "…");
}
