using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;

namespace ExchangeIntegration.Tests;

public class MessageKeysTests
{
    [Fact]
    public void The_key_is_32_lowercase_hex_characters_and_stable()
    {
        var key = MessageKeys.Compute("ap-invoices@contoso.com", "<inv-1001@example.test>");

        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal(key, MessageKeys.Compute("  AP-Invoices@Contoso.com ", " <inv-1001@example.test> "));
        Assert.NotEqual(key, MessageKeys.Compute("ap-invoices@contoso.com", "<INV-1001@example.test>"));
        Assert.NotEqual(key, MessageKeys.Compute("other@contoso.com", "<inv-1001@example.test>"));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.reply", true)]
    [InlineData("0123456789abcdef0123456789abcdef.move-processed_2", true)]
    [InlineData("0123456789ABCDEF0123456789abcdef.reply", false)]
    [InlineData("0123456789abcdef0123456789abcdef.", false)]
    [InlineData("0123456789abcdef0123456789abcdef.a/b", false)]
    [InlineData("0123456789abcdef0123456789abcdef", false)]
    [InlineData("reply-1", false)]
    [InlineData(null, false)]
    public void Command_ids_are_the_message_key_plus_a_short_suffix(string? commandId, bool expected) =>
        Assert.Equal(expected, MessageKeys.IsWellFormedCommandId(commandId));

    [Theory]
    [InlineData("Invoice 1001.pdf", "Invoice_1001.pdf")]
    [InlineData("../../etc/passwd", "_.._etc_passwd")]
    [InlineData("rechnung-ä.pdf", "rechnung-_.pdf")]
    [InlineData("", "attachment")]
    [InlineData(null, "attachment")]
    [InlineData("...", "attachment")]
    public void Attachment_file_names_are_made_safe_for_blob_names(string? name, string expected) =>
        Assert.Equal(expected, BlobPaths.SafeFileName(name));

    [Fact]
    public void Long_file_names_are_cut_but_keep_their_extension()
    {
        var safe = BlobPaths.SafeFileName(new string('a', 300) + ".pdf");

        Assert.Equal(BlobPaths.MaxFileNameLength, safe.Length);
        Assert.EndsWith(".pdf", safe);
    }

    [Fact]
    public void Blob_names_are_grouped_under_the_message_key()
    {
        Assert.Equal("k/manifest.json", BlobPaths.Manifest("k"));
        Assert.Equal("k/attachments/03-a.pdf", BlobPaths.Attachment("k", 3, "a.pdf"));
        Assert.Equal("k/commands/k.reply.json", BlobPaths.CommandRecord("k", "k.reply"));
    }
}

public class AzureOptionsValidatorTests
{
    private static AzureOptions ValidOptions() => new()
    {
        BlobServiceUri = "https://contosoinvoices.blob.core.windows.net",
        ServiceBusNamespace = "contoso-invoices.servicebus.windows.net",
    };

    [Fact]
    public void Valid_options_with_default_names_pass()
    {
        Assert.Empty(AzureOptionsValidator.GetFailures(ValidOptions()));
        Assert.True(new AzureOptionsValidator().Validate(null, ValidOptions()).Succeeded);
    }

    [Fact]
    public void Missing_endpoints_are_rejected()
    {
        var failures = AzureOptionsValidator.GetFailures(new AzureOptions());

        Assert.Contains(failures, failure => failure.StartsWith("Azure:BlobServiceUri is required"));
        Assert.Contains(failures, failure => failure.StartsWith("Azure:ServiceBusNamespace is required"));
    }

    [Theory]
    [InlineData("http://contosoinvoices.blob.core.windows.net", "must be an https:// URL")]
    [InlineData("contosoinvoices.blob.core.windows.net", "must be an https:// URL")]
    [InlineData("https://contosoinvoices.blob.core.windows.net/?sv=2024-01-01&sig=abc", "SAS token")]
    [InlineData("https://user:pass@contosoinvoices.blob.core.windows.net", "credentials")]
    public void The_blob_endpoint_must_be_plain_https(string uri, string expected)
    {
        var options = ValidOptions();
        options.BlobServiceUri = uri;

        Assert.Contains(AzureOptionsValidator.GetFailures(options), failure => failure.Contains(expected));
    }

    [Theory]
    [InlineData("Endpoint=sb://contoso-invoices.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y", "not a connection string")]
    [InlineData("sb://contoso-invoices.servicebus.windows.net", "fully qualified host name")]
    [InlineData("contoso-invoices", "fully qualified host name")]
    public void The_service_bus_namespace_must_be_a_host_name(string value, string expected)
    {
        var options = ValidOptions();
        options.ServiceBusNamespace = value;

        var failures = AzureOptionsValidator.GetFailures(options);

        Assert.Contains(failures, failure => failure.Contains(expected));
        Assert.DoesNotContain(failures, failure => failure.Contains("SharedAccessKey=y"));
    }

    [Theory]
    [InlineData("Invoice-Intake")]
    [InlineData("ab")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("")]
    public void Container_and_queue_names_are_checked(string name)
    {
        var options = ValidOptions();
        options.IntakeContainer = name;
        options.CommandsQueue = name;

        var failures = AzureOptionsValidator.GetFailures(options);

        Assert.Contains(failures, failure => failure.StartsWith("Azure:IntakeContainer"));
        Assert.Contains(failures, failure => failure.StartsWith("Azure:CommandsQueue"));
    }
}
