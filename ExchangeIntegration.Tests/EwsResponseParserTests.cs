using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;

namespace ExchangeIntegration.Tests;

public class EwsResponseParserTests
{
    [Fact]
    public void Successful_get_folder_response_is_parsed()
    {
        var parsed = EwsResponseParser.Parse(SoapSamples.Success());

        Assert.Null(parsed.Fault);
        var getFolder = Assert.IsType<EwsGetFolderResponse>(parsed.GetFolder);
        Assert.Equal("Success", getFolder.ResponseClass);
        Assert.Equal("NoError", getFolder.ResponseCode);
        Assert.Equal(SoapSamples.FolderId, getFolder.FolderId);
        Assert.Equal("Inbox", getFolder.FolderDisplayName);
        Assert.Equal("15.2.2562.17", parsed.ServerVersion);
    }

    [Fact]
    public void Error_response_message_keeps_its_code_and_text()
    {
        var parsed = EwsResponseParser.Parse(SoapSamples.AccessDenied());

        var getFolder = Assert.IsType<EwsGetFolderResponse>(parsed.GetFolder);
        Assert.Equal("Error", getFolder.ResponseClass);
        Assert.Equal("ErrorAccessDenied", getFolder.ResponseCode);
        Assert.Equal("Access is denied. Check credentials and try again.", getFolder.MessageText);
        Assert.Null(getFolder.FolderId);
    }

    [Fact]
    public void Soap_fault_exposes_the_ews_error_code()
    {
        var parsed = EwsResponseParser.Parse(SoapSamples.Fault("ErrorInvalidServerVersion", "The specified server version is invalid."));

        Assert.Null(parsed.GetFolder);
        var fault = Assert.IsType<EwsSoapFault>(parsed.Fault);
        Assert.Equal("ErrorInvalidServerVersion", fault.ResponseCode);
        Assert.Equal("The specified server version is invalid.", fault.Message);
    }

    [Fact]
    public void Fault_without_detail_falls_back_to_the_ews_qualified_fault_code()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <s:Fault>
                  <faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:ErrorSchemaValidation</faultcode>
                  <faultstring>Invalid request.</faultstring>
                </s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        var fault = Assert.IsType<EwsSoapFault>(EwsResponseParser.Parse(xml).Fault);

        Assert.Equal("ErrorSchemaValidation", fault.ResponseCode);
    }

    [Fact]
    public void Plain_soap_fault_code_is_not_mistaken_for_an_ews_code()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <s:Fault><faultcode>s:Client</faultcode><faultstring>Bad request.</faultstring></s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        var fault = Assert.IsType<EwsSoapFault>(EwsResponseParser.Parse(xml).Fault);

        Assert.Null(fault.ResponseCode);
        Assert.Equal("Bad request.", fault.FaultString);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not xml")]
    [InlineData("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>")]
    public void Empty_or_malformed_xml_is_a_protocol_error(string body)
    {
        Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(body));
    }

    [Fact]
    public void Xml_that_is_not_a_soap_envelope_is_a_protocol_error()
    {
        Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(SoapSamples.SignInPage));
    }

    [Fact]
    public void Envelope_without_a_get_folder_response_is_a_protocol_error()
    {
        Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(SoapSamples.EnvelopeWithoutGetFolderResponse));
    }

    [Fact]
    public void Doctype_declarations_are_refused()
    {
        // Placed after the XML declaration so that the DTD itself, not a misplaced declaration, is what gets refused.
        var xml = SoapSamples.Success().Replace("<s:Envelope", "<!DOCTYPE s:Envelope [<!ENTITY x \"expand\">]><s:Envelope");

        var exception = Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(xml));

        Assert.Contains("DTD", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("")]
    public void Unknown_response_class_is_a_protocol_error(string responseClass)
    {
        var xml = SoapSamples.Success().Replace("ResponseClass=\"Success\"", $"ResponseClass=\"{responseClass}\"");

        Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(xml));
    }

    [Theory]
    [InlineData("No Error")]
    [InlineData("Error<script>")]
    [InlineData("")]
    public void Malformed_response_code_is_a_protocol_error(string responseCode)
    {
        var xml = SoapSamples.Success().Replace("<m:ResponseCode>NoError</m:ResponseCode>", $"<m:ResponseCode>{responseCode.Replace("<", "&lt;")}</m:ResponseCode>");

        Assert.Throws<EwsProtocolException>(() => EwsResponseParser.Parse(xml));
    }

    [Fact]
    public void Server_version_is_omitted_when_the_header_is_not_numeric()
    {
        var xml = SoapSamples.Success().Replace("MajorVersion=\"15\"", "MajorVersion=\"x15\"");

        Assert.Null(EwsResponseParser.Parse(xml).ServerVersion);
    }
}
