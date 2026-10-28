using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("a\r\nb\t c", "a b c")]
    [InlineData("  padded   text  ", "padded text")]
    public void Messages_are_flattened_to_a_single_clean_line(string? input, string expected)
    {
        Assert.Equal(expected, Redaction.SanitizeMessage(input));
    }

    [Fact]
    public void Control_characters_cannot_forge_log_lines()
    {
        var sanitized = Redaction.SanitizeMessage("first\u0000\u001b[31m\nINFO forged entry");

        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\u001b', sanitized);
        Assert.DoesNotContain('\u0000', sanitized);
    }

    [Fact]
    public void Token_like_runs_are_masked()
    {
        var sanitized = Redaction.SanitizeMessage("token eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9eyJzdWIiOiIxMjM0NTY3ODkwIn0 end");

        Assert.Equal("token [redacted] end", sanitized);
    }

    [Fact]
    public void Ordinary_words_and_short_identifiers_are_not_masked()
    {
        const string text = "The SMTP address has no mailbox associated with it. See mail.contoso.com/EWS/Exchange.asmx";

        Assert.Equal(text, Redaction.SanitizeMessage(text));
    }

    [Fact]
    public void Long_messages_are_truncated_with_an_ellipsis()
    {
        var sanitized = Redaction.SanitizeMessage(string.Join(' ', Enumerable.Repeat("word", 400)));

        Assert.Equal(Redaction.MaxMessageLength + 3, sanitized.Length);
        Assert.EndsWith("...", sanitized);
    }

    [Fact]
    public void Fingerprints_are_short_stable_and_not_reversible_to_the_identifier()
    {
        var first = Redaction.Fingerprint("identifier-one");

        Assert.Equal(first, Redaction.Fingerprint("identifier-one"));
        Assert.NotEqual(first, Redaction.Fingerprint("identifier-two"));
        Assert.Equal(8, first.Length);
        Assert.Matches("^[0-9A-F]{8}$", first);
        Assert.DoesNotContain("identifier", first);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_identifiers_have_no_fingerprint(string? identifier)
    {
        Assert.Equal("none", Redaction.Fingerprint(identifier));
    }
}
