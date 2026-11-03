using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeIntegration.Adapter;

/// <summary>The "type" values of the JSON messages exchanged with the AI Workflow Engine.</summary>
public static class MessageTypes
{
    public const string InvoiceReceived = "invoice.received";
    public const string Reply = "exchange.command.reply";
    public const string Move = "exchange.command.move";
    public const string CommandResult = "exchange.command.result";
}

/// <summary>camelCase JSON, lenient on read (case-insensitive names), no nulls written.</summary>
public static class AdapterJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Adapter → Engine on the invoice-received queue. Points at the manifest; carries no email content.</summary>
public sealed class InvoiceReceivedMessage
{
    public string Type { get; init; } = MessageTypes.InvoiceReceived;

    public required string MessageKey { get; init; }

    public required string Mailbox { get; init; }

    public required string InternetMessageId { get; init; }

    public required DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>Blob URL of manifest.json (no SAS; the Engine reads it with its own identity).</summary>
    public required Uri Manifest { get; init; }
}

/// <summary>Written to &lt;messageKey&gt;/manifest.json after every attachment is stored.</summary>
public sealed class InvoiceManifest
{
    public required string MessageKey { get; init; }

    public required string Mailbox { get; init; }

    public required string InternetMessageId { get; init; }

    public required DateTimeOffset ReceivedUtc { get; init; }

    /// <summary>The From address as Exchange reports it. Untrusted.</summary>
    public string? From { get; init; }

    /// <summary>Untrusted.</summary>
    public string? Subject { get; init; }

    public required IReadOnlyList<ManifestFile> Files { get; init; }

    /// <summary>Attachments that were not stored, such as attached emails (item attachments).</summary>
    public int SkippedAttachments { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }
}

public sealed class ManifestFile
{
    /// <summary>The attachment's original file name. Untrusted.</summary>
    public required string Name { get; init; }

    /// <summary>Blob name inside the intake container.</summary>
    public required string Blob { get; init; }

    public required string ContentType { get; init; }

    public required long Size { get; init; }

    public bool IsInline { get; init; }
}

/// <summary>Engine → Adapter on the exchange-commands queue. Everything is optional here and checked by the handler.</summary>
public sealed class ExchangeCommand
{
    public string? Type { get; init; }

    public string? CommandId { get; init; }

    public string? MessageKey { get; init; }

    public string? InternetMessageId { get; init; }

    public string? Mailbox { get; init; }

    public ReplyDetails? Reply { get; init; }

    public MoveDetails? Move { get; init; }
}

public sealed class ReplyDetails
{
    public string? BodyText { get; init; }
}

public sealed class MoveDetails
{
    public string? Folder { get; init; }
}

/// <summary>Adapter → Engine on the exchange-command-results queue.</summary>
public sealed class CommandResultMessage
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public string Type { get; init; } = MessageTypes.CommandResult;

    public required string CommandId { get; init; }

    public string? MessageKey { get; init; }

    public required string Status { get; init; }

    /// <summary>Why the command failed; null on success.</summary>
    public string? Reason { get; init; }

    /// <summary>True when sending the same command again later may succeed.</summary>
    public bool Retryable { get; init; }

    public required DateTimeOffset CompletedUtc { get; init; }
}

/// <summary>Stored at &lt;messageKey&gt;/commands/&lt;commandId&gt;.json so a repeated command is recognized.</summary>
public sealed class CommandRecord
{
    public const string Started = "started";
    public const string Succeeded = "succeeded";

    public required string Status { get; init; }

    public CommandResultMessage? Result { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }
}
