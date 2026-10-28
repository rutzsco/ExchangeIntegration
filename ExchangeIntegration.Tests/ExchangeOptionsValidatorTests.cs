using ExchangeIntegration.Configuration;

namespace ExchangeIntegration.Tests;

public class ExchangeOptionsValidatorTests
{
    private static ExchangeOptions ValidOptions() => new()
    {
        EwsUrl = "https://mail.contoso.com/EWS/Exchange.asmx",
        Mailbox = "ap-invoices@contoso.com",
        AccessMode = ExchangeAccessMode.Delegated,
        TimeoutSeconds = 30,
    };

    [Fact]
    public void Valid_options_pass()
    {
        Assert.Empty(ExchangeOptionsValidator.GetFailures(ValidOptions()));
        Assert.True(new ExchangeOptionsValidator().Validate(null, ValidOptions()).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_ews_url_is_rejected(string url)
    {
        var options = ValidOptions();
        options.EwsUrl = url;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("Exchange:EwsUrl is required"));
    }

    [Theory]
    [InlineData("http://mail.contoso.com/EWS/Exchange.asmx")]
    [InlineData("ftp://mail.contoso.com/EWS/Exchange.asmx")]
    public void Ews_url_must_use_https(string url)
    {
        var options = ValidOptions();
        options.EwsUrl = url;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("must use HTTPS"));
    }

    [Theory]
    [InlineData("mail.contoso.com/EWS/Exchange.asmx")]
    [InlineData("not a url")]
    public void Ews_url_must_be_an_absolute_url(string url)
    {
        var options = ValidOptions();
        options.EwsUrl = url;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("Exchange:EwsUrl"));
    }

    [Fact]
    public void Embedded_credentials_in_the_url_are_rejected_and_never_echoed()
    {
        var options = ValidOptions();
        options.EwsUrl = "https://svc-user:S3cretP4ss@mail.contoso.com/EWS/Exchange.asmx";

        var failures = ExchangeOptionsValidator.GetFailures(options);

        Assert.Contains(failures, failure => failure.Contains("embedded credentials"));
        Assert.DoesNotContain(failures, failure => failure.Contains("S3cretP4ss") || failure.Contains("svc-user"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ap-invoices")]
    [InlineData("ap-invoices@")]
    [InlineData("a@b@contoso.com")]
    [InlineData("AP Invoices <ap-invoices@contoso.com>")]
    [InlineData(" ap-invoices@contoso.com ")]
    public void Mailbox_must_be_a_plain_smtp_address(string mailbox)
    {
        var options = ValidOptions();
        options.Mailbox = mailbox;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("Exchange:Mailbox"));
    }

    [Theory]
    [InlineData("ap-invoices@contoso.com")]
    [InlineData("first.last+tag@mail.contoso.com")]
    public void Plain_smtp_addresses_are_accepted(string mailbox)
    {
        var options = ValidOptions();
        options.Mailbox = mailbox;

        Assert.Empty(ExchangeOptionsValidator.GetFailures(options));
    }

    [Fact]
    public void Impersonation_is_recognized_but_not_supported_in_this_milestone()
    {
        var options = ValidOptions();
        options.AccessMode = ExchangeAccessMode.Impersonation;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("not supported in this milestone"));
    }

    [Fact]
    public void Undefined_access_mode_is_rejected()
    {
        var options = ValidOptions();
        options.AccessMode = (ExchangeAccessMode)42;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("not recognized"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(ExchangeOptions.MaxTimeoutSeconds + 1)]
    public void Timeout_outside_the_allowed_range_is_rejected(int timeoutSeconds)
    {
        var options = ValidOptions();
        options.TimeoutSeconds = timeoutSeconds;

        Assert.Contains(ExchangeOptionsValidator.GetFailures(options), failure => failure.Contains("Exchange:TimeoutSeconds"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(ExchangeOptions.MaxTimeoutSeconds)]
    public void Timeout_inside_the_allowed_range_is_accepted(int timeoutSeconds)
    {
        var options = ValidOptions();
        options.TimeoutSeconds = timeoutSeconds;

        Assert.Empty(ExchangeOptionsValidator.GetFailures(options));
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var options = new ExchangeOptions
        {
            EwsUrl = "http://mail.contoso.com/EWS/Exchange.asmx",
            Mailbox = "",
            AccessMode = ExchangeAccessMode.Impersonation,
            TimeoutSeconds = 0,
        };

        var result = new ExchangeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Equal(4, result.Failures!.Count());
    }

    [Fact]
    public void Display_endpoint_drops_user_info_query_and_fragment()
    {
        var options = new ExchangeOptions { EwsUrl = "https://user:pw@mail.contoso.com:8443/EWS/Exchange.asmx?token=abc#frag" };

        var display = options.GetDisplayEndpoint();

        Assert.Equal("https://mail.contoso.com:8443/EWS/Exchange.asmx", display);
    }
}
