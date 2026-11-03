using System.Text.Json;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Adapter;

/// <summary>The result of handling one command message: a result was published, or the message must be dead-lettered.</summary>
public sealed record CommandOutcome(CommandResultMessage? Result, string? DeadLetterReason = null, string? DeadLetterDescription = null)
{
    public static CommandOutcome DeadLetter(string reason, string description) => new(null, reason, description);
}

/// <summary>
/// Carries out one Engine command (reply or move) and publishes its result. Every command is checked against the
/// configured mailbox and allow-list first, because the queue is a trust boundary. Exceptions from Blob Storage or
/// Service Bus propagate so the message is abandoned and redelivered.
/// </summary>
public sealed class ExchangeCommandHandler
{
    public const int MaxReplyLength = 20_000;

    private readonly IExchangeMailbox _mailbox;
    private readonly IInvoiceStore _store;
    private readonly IEngineQueues _queues;
    private readonly ExchangeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExchangeCommandHandler> _logger;

    public ExchangeCommandHandler(
        IExchangeMailbox mailbox,
        IInvoiceStore store,
        IEngineQueues queues,
        IOptions<ExchangeOptions> options,
        TimeProvider time,
        ILogger<ExchangeCommandHandler> logger)
    {
        _mailbox = mailbox;
        _store = store;
        _queues = queues;
        _options = options.Value;
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
        var invalid = Validate(command, commandId, out var messageKey, out var folder);
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
            var itemIds = await _mailbox.FindInboxItemIdsAsync(command.InternetMessageId!, cancellationToken);
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
                await _mailbox.MoveAsync([itemIds[0]], folder!, cancellationToken);
            }
            else
            {
                var refusal = await ReplyAsync(command, itemIds[0], recordBlob, cancellationToken);
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
    private async Task<string?> ReplyAsync(ExchangeCommand command, string itemId, string recordBlob, CancellationToken cancellationToken)
    {
        var original = await _mailbox.GetMessageAsync(itemId, cancellationToken);

        if (original.From is null || string.IsNullOrWhiteSpace(original.From.Address))
        {
            return "The email has no sender address to reply to.";
        }

        if (string.Equals(original.From.Address.Trim(), _options.Mailbox.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "The email was sent by the monitored mailbox itself; replying would start a loop.";
        }

        if (original.AutoSubmitted is { } autoSubmitted && !string.Equals(autoSubmitted, "no", StringComparison.OrdinalIgnoreCase))
        {
            return "The email is automatic (Auto-Submitted header); replying would risk a mail loop.";
        }

        // At most once: the started record stays if the outcome of the send is unknown, so a redelivery cannot send twice.
        await WriteRecordAsync(recordBlob, new CommandRecord { Status = CommandRecord.Started, UpdatedUtc = _time.GetUtcNow() }, cancellationToken);
        try
        {
            await _mailbox.SendReplyAsync(original, command.Reply!.BodyText!, CancellationToken.None);
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

    private string? Validate(ExchangeCommand command, string commandId, out string? messageKey, out string? folder)
    {
        // The well-formed commandId starts with a 32-hex key; echo that rather than an arbitrary messageKey string.
        messageKey = commandId[..32];
        folder = null;

        if (command.Type is not (MessageTypes.Reply or MessageTypes.Move))
        {
            return $"Unknown command type '{Truncate(command.Type)}'.";
        }

        if (!string.Equals(command.Mailbox?.Trim(), _options.Mailbox.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "The command is for a different mailbox than the one this adapter serves.";
        }

        if (string.IsNullOrWhiteSpace(command.InternetMessageId))
        {
            return "internetMessageId is required.";
        }

        var expectedKey = MessageKeys.Compute(_options.Mailbox, command.InternetMessageId);
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
        folder = _options.GetMoveFolders().FirstOrDefault(allowed => string.Equals(allowed, requested, StringComparison.OrdinalIgnoreCase));
        return folder is null ? $"move.folder '{Truncate(requested)}' is not in Exchange:MoveFolders." : null;
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
