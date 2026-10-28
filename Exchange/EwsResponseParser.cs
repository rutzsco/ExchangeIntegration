using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ExchangeIntegration.Exchange;

/// <summary>The response is not a usable EWS SOAP response (malformed XML or an unexpected structure).</summary>
public sealed class EwsProtocolException : Exception
{
    public EwsProtocolException(string message)
        : base(message)
    {
    }

    public EwsProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The GetFolderResponseMessage of a GetFolder response.</summary>
public sealed class EwsGetFolderResponse
{
    public required string ResponseClass { get; init; }

    public required string ResponseCode { get; init; }

    public string? MessageText { get; init; }

    public string? FolderId { get; init; }

    public string? FolderDisplayName { get; init; }
}

/// <summary>A SOAP fault returned by EWS, typically together with HTTP 500.</summary>
public sealed class EwsSoapFault
{
    public string? FaultString { get; init; }

    /// <summary>The EWS error code, for example ErrorInvalidServerVersion, when the fault carries one.</summary>
    public string? ResponseCode { get; init; }

    public string? Message { get; init; }
}

/// <summary>A parsed EWS response: either a fault or a GetFolder response message.</summary>
public sealed class EwsParsedResponse
{
    public string? ServerVersion { get; init; }

    public EwsGetFolderResponse? GetFolder { get; init; }

    public EwsSoapFault? Fault { get; init; }
}

/// <summary>Parses EWS SOAP responses. Nothing here logs or retains the raw XML.</summary>
public static class EwsResponseParser
{
    private static readonly XNamespace Soap = EwsRequestBuilder.SoapNamespaceName;
    private static readonly XNamespace Messages = EwsRequestBuilder.MessagesNamespaceName;
    private static readonly XNamespace Types = EwsRequestBuilder.TypesNamespaceName;

    // EWS response codes are identifiers such as ErrorAccessDenied; anything else is not trusted as a code.
    private static readonly Regex CodePattern = new(
        "^[A-Za-z][A-Za-z0-9_]{0,99}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    /// <exception cref="EwsProtocolException">The text is not a well-formed EWS SOAP response.</exception>
    public static EwsParsedResponse Parse(string soapXml)
    {
        if (string.IsNullOrWhiteSpace(soapXml))
        {
            throw new EwsProtocolException("The response body was empty.");
        }

        var envelope = LoadEnvelope(soapXml);
        var body = envelope.Element(Soap + "Body")
            ?? throw new EwsProtocolException("The SOAP envelope has no Body element.");
        var serverVersion = ReadServerVersion(envelope.Element(Soap + "Header"));

        var fault = body.Element(Soap + "Fault");
        if (fault is not null)
        {
            return new EwsParsedResponse { ServerVersion = serverVersion, Fault = ReadFault(fault) };
        }

        var getFolderResponse = body.Element(Messages + "GetFolderResponse")
            ?? throw new EwsProtocolException("The SOAP body does not contain a GetFolderResponse element.");

        var responseMessage = getFolderResponse.Element(Messages + "ResponseMessages")?.Element(Messages + "GetFolderResponseMessage")
            ?? throw new EwsProtocolException("The GetFolderResponse does not contain a GetFolderResponseMessage.");

        var responseClass = ((string?)responseMessage.Attribute("ResponseClass"))?.Trim();
        var responseCode = ((string?)responseMessage.Element(Messages + "ResponseCode"))?.Trim();

        if (responseClass is not ("Success" or "Warning" or "Error"))
        {
            throw new EwsProtocolException("The GetFolderResponseMessage has a missing or unknown ResponseClass.");
        }

        if (responseCode is null || !CodePattern.IsMatch(responseCode))
        {
            throw new EwsProtocolException("The GetFolderResponseMessage has a missing or malformed ResponseCode.");
        }

        var folder = responseMessage.Element(Messages + "Folders")?.Elements().FirstOrDefault();

        return new EwsParsedResponse
        {
            ServerVersion = serverVersion,
            GetFolder = new EwsGetFolderResponse
            {
                ResponseClass = responseClass,
                ResponseCode = responseCode,
                MessageText = (string?)responseMessage.Element(Messages + "MessageText"),
                FolderId = NullIfBlank((string?)folder?.Element(Types + "FolderId")?.Attribute("Id")),
                FolderDisplayName = NullIfBlank((string?)folder?.Element(Types + "DisplayName")),
            },
        };
    }

    private static XElement LoadEnvelope(string soapXml)
    {
        XDocument document;
        try
        {
            // A DOCTYPE never appears in an EWS response, so refuse it rather than risk entity expansion.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stringReader = new StringReader(soapXml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader);
        }
        catch (XmlException exception)
        {
            throw new EwsProtocolException("The response body is not well-formed XML.", exception);
        }

        if (document.Root is not { } root || root.Name != Soap + "Envelope")
        {
            throw new EwsProtocolException("The response is not a SOAP envelope.");
        }

        return root;
    }

    private static string? ReadServerVersion(XElement? header)
    {
        var info = header?.Element(Types + "ServerVersionInfo");
        if (info is null)
        {
            return null;
        }

        string?[] parts =
        [
            (string?)info.Attribute("MajorVersion"),
            (string?)info.Attribute("MinorVersion"),
            (string?)info.Attribute("MajorBuildNumber"),
            (string?)info.Attribute("MinorBuildNumber"),
        ];

        return parts.All(part => !string.IsNullOrEmpty(part) && part.All(char.IsAsciiDigit))
            ? string.Join('.', parts)
            : null;
    }

    private static EwsSoapFault ReadFault(XElement fault)
    {
        var detail = fault.Element("detail");
        var detailCode = detail?.Elements().FirstOrDefault(element => element.Name.LocalName == "ResponseCode")?.Value.Trim();
        var detailMessage = detail?.Elements().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value;

        var code = detailCode ?? ReadEwsCodeFromFaultCode(fault.Element("faultcode"));

        return new EwsSoapFault
        {
            FaultString = fault.Element("faultstring")?.Value,
            ResponseCode = code is not null && CodePattern.IsMatch(code) ? code : null,
            Message = detailMessage,
        };
    }

    // EWS faults carry a qualified name such as "a:ErrorInvalidServerVersion" whose prefix maps to the EWS types
    // namespace; a plain SOAP code such as "s:Client" is not an EWS code and is ignored.
    private static string? ReadEwsCodeFromFaultCode(XElement? faultCode)
    {
        var value = faultCode?.Value.Trim();
        var separator = value?.IndexOf(':') ?? -1;
        if (faultCode is null || value is null || separator <= 0)
        {
            return null;
        }

        return faultCode.GetNamespaceOfPrefix(value[..separator]) == Types ? value[(separator + 1)..] : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
