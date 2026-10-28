using System.Xml.Linq;
using ExchangeIntegration.Configuration;

namespace ExchangeIntegration.Exchange;

/// <summary>
/// Builds EWS SOAP requests. This is the one place where SOAP headers are produced, which is where scoped
/// ExchangeImpersonation will be added in a later milestone.
/// </summary>
public static class EwsRequestBuilder
{
    public const string SoapNamespaceName = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string MessagesNamespaceName = "http://schemas.microsoft.com/exchange/services/2006/messages";
    public const string TypesNamespaceName = "http://schemas.microsoft.com/exchange/services/2006/types";

    // Oldest schema version that Exchange 2013, 2016, 2019 and Subscription Edition all accept.
    public const string RequestedServerVersion = "Exchange2013_SP1";

    private static readonly XNamespace Soap = SoapNamespaceName;
    private static readonly XNamespace Messages = MessagesNamespaceName;
    private static readonly XNamespace Types = TypesNamespaceName;

    /// <summary>
    /// GetFolder for the Inbox (DistinguishedFolderId with a Mailbox child), asking for the folder identifier and
    /// display name only. Nothing in the request can modify the mailbox.
    /// </summary>
    public static string BuildGetInboxFolderRequest(string mailbox, ExchangeAccessMode accessMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailbox);

        if (accessMode != ExchangeAccessMode.Delegated)
        {
            throw new NotSupportedException($"Access mode '{accessMode}' is not supported in this milestone; only Delegated is implemented.");
        }

        var envelope = new XElement(
            Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", Soap),
            new XAttribute(XNamespace.Xmlns + "m", Messages),
            new XAttribute(XNamespace.Xmlns + "t", Types),
            new XElement(
                Soap + "Header",
                new XElement(Types + "RequestServerVersion", new XAttribute("Version", RequestedServerVersion))),
            new XElement(
                Soap + "Body",
                new XElement(
                    Messages + "GetFolder",
                    new XElement(
                        Messages + "FolderShape",
                        new XElement(Types + "BaseShape", "IdOnly"),
                        new XElement(
                            Types + "AdditionalProperties",
                            new XElement(Types + "FieldURI", new XAttribute("FieldURI", "folder:DisplayName")))),
                    new XElement(
                        Messages + "FolderIds",
                        new XElement(
                            Types + "DistinguishedFolderId",
                            new XAttribute("Id", "inbox"),
                            new XElement(
                                Types + "Mailbox",
                                new XElement(Types + "EmailAddress", mailbox)))))));

        return new XDeclaration("1.0", "utf-8", null) + Environment.NewLine + envelope.ToString(SaveOptions.DisableFormatting);
    }
}
