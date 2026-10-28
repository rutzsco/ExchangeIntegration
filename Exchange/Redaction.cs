using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ExchangeIntegration.Exchange;

/// <summary>Keeps text that originates outside the process, and identifiers, safe to print or log.</summary>
public static class Redaction
{
    public const int MaxMessageLength = 300;

    // Long unbroken base64/base64url-like runs are tokens or identifiers, never part of a human-readable message.
    private static readonly Regex LongTokenPattern = new(
        @"[A-Za-z0-9+/_=\-]{40,}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Makes a server-supplied message safe for a log line or console: control characters (log injection) and
    /// repeated whitespace are collapsed, token-like blobs are masked, and the length is capped.
    /// </summary>
    public static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(message.Length);
        foreach (var character in message)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var text = WhitespacePattern.Replace(builder.ToString(), " ").Trim();
        text = LongTokenPattern.Replace(text, "[redacted]");

        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength] + "...";
    }

    /// <summary>A short one-way fingerprint that lets runs be compared without printing the identifier itself.</summary>
    public static string Fingerprint(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(hash, 0, 4);
    }
}
