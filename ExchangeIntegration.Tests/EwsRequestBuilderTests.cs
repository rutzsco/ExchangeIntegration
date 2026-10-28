using System.Xml.Linq;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;

namespace ExchangeIntegration.Tests;

public class EwsRequestBuilderTests
{
    private static readonly XNamespace Soap = EwsRequestBuilder.SoapNamespaceName;
    private static readonly XNamespace Messages = EwsRequestBuilder.MessagesNamespaceName;
    private static readonly XNamespace Types = EwsRequestBuilder.TypesNamespaceName;

    private static XDocument Build(string mailbox = "ap-invoices@contoso.com") =>
        XDocument.Parse(EwsRequestBuilder.BuildGetInboxFolderRequest(mailbox, ExchangeAccessMode.Delegated));

    [Fact]
    public void Request_is_a_get_folder_for_the_inbox_of_the_configured_mailbox()
    {
        var body = Build().Root!.Element(Soap + "Body")!;

        var getFolder = Assert.Single(body.Elements());
        Assert.Equal(Messages + "GetFolder", getFolder.Name);

        var folderId = Assert.Single(getFolder.Element(Messages + "FolderIds")!.Elements());
        Assert.Equal(Types + "DistinguishedFolderId", folderId.Name);
        Assert.Equal("inbox", (string?)folderId.Attribute("Id"));
        Assert.Equal(
            "ap-invoices@contoso.com",
            (string?)folderId.Element(Types + "Mailbox")!.Element(Types + "EmailAddress"));
    }

    [Fact]
    public void Request_asks_only_for_the_folder_identifier_and_display_name()
    {
        var shape = Build().Root!.Element(Soap + "Body")!.Element(Messages + "GetFolder")!.Element(Messages + "FolderShape")!;

        Assert.Equal("IdOnly", (string?)shape.Element(Types + "BaseShape"));
        var property = Assert.Single(shape.Element(Types + "AdditionalProperties")!.Elements());
        Assert.Equal("folder:DisplayName", (string?)property.Attribute("FieldURI"));
    }

    [Fact]
    public void Request_cannot_modify_the_mailbox_or_read_message_content()
    {
        var xml = EwsRequestBuilder.BuildGetInboxFolderRequest("ap-invoices@contoso.com", ExchangeAccessMode.Delegated);

        foreach (var forbidden in new[] { "CreateItem", "DeleteItem", "MoveItem", "CopyItem", "UpdateItem", "SendItem", "FindItem", "GetItem", "GetAttachment", "item:Body", "item:Attachments" })
        {
            Assert.DoesNotContain(forbidden, xml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Delegated_access_adds_no_impersonation_header()
    {
        var header = Build().Root!.Element(Soap + "Header")!;

        var only = Assert.Single(header.Elements());
        Assert.Equal(Types + "RequestServerVersion", only.Name);
        Assert.Equal(EwsRequestBuilder.RequestedServerVersion, (string?)only.Attribute("Version"));
    }

    [Fact]
    public void Impersonation_is_not_implemented_in_this_milestone()
    {
        Assert.Throws<NotSupportedException>(
            () => EwsRequestBuilder.BuildGetInboxFolderRequest("ap-invoices@contoso.com", ExchangeAccessMode.Impersonation));
    }

    [Fact]
    public void Mailbox_text_is_xml_escaped()
    {
        const string mailbox = "a&b<c>@contoso.com";

        var parsed = Build(mailbox);

        Assert.Equal(mailbox, parsed.Descendants(Types + "EmailAddress").Single().Value);
    }

    [Fact]
    public void Request_declares_utf8_xml()
    {
        var xml = EwsRequestBuilder.BuildGetInboxFolderRequest("ap-invoices@contoso.com", ExchangeAccessMode.Delegated);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_mailbox_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => EwsRequestBuilder.BuildGetInboxFolderRequest(" ", ExchangeAccessMode.Delegated));
    }
}
