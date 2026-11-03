using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ExchangeIntegration.Adapter;

/// <summary>
/// The stable identity of one email: the first 32 hex characters of SHA-256 over the lower-cased mailbox, a newline
/// and the trimmed Message-ID header. It names the blob folder, is the invoice.received message ID and prefixes every
/// commandId, so a repeat of anything is recognized.
/// </summary>
public static class MessageKeys
{
    private static readonly Regex CommandIdPattern = new(
        @"^[0-9a-f]{32}\.[A-Za-z0-9_-]{1,64}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static string Compute(string mailbox, string internetMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(internetMessageId);

        var input = mailbox.Trim().ToLowerInvariant() + "\n" + internetMessageId.Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)), 0, 16);
    }

    /// <summary>&lt;messageKey&gt;.&lt;suffix&gt;, for example 3f9a…62.reply; the suffix is up to 64 of A-Z a-z 0-9 _ -.</summary>
    public static bool IsWellFormedCommandId(string? commandId) => commandId is not null && CommandIdPattern.IsMatch(commandId);
}

/// <summary>Blob names inside the intake container, all under the email's messageKey.</summary>
public static class BlobPaths
{
    public const int MaxFileNameLength = 100;

    public static string Manifest(string messageKey) => $"{messageKey}/manifest.json";

    public static string Attachment(string messageKey, int index, string? fileName) =>
        $"{messageKey}/attachments/{index:D2}-{SafeFileName(fileName)}";

    public static string CommandRecord(string messageKey, string commandId) => $"{messageKey}/commands/{commandId}.json";

    /// <summary>Keeps A-Z a-z 0-9 . _ - (everything else becomes _), caps the length and keeps a short extension.</summary>
    public static string SafeFileName(string? fileName)
    {
        var safe = new string((fileName ?? string.Empty).Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());
        safe = safe.Trim('.');

        if (safe.Length > MaxFileNameLength)
        {
            var extension = Path.GetExtension(safe);
            extension = extension.Length is > 1 and <= 10 ? extension : string.Empty;
            safe = safe[..(MaxFileNameLength - extension.Length)] + extension;
        }

        return safe.Length == 0 ? "attachment" : safe;
    }
}
