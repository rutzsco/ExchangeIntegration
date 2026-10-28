namespace ExchangeIntegration.Tests.TestSupport;

/// <summary>EWS responses shaped like those of Exchange Server. Every identifier here is fake.</summary>
public static class SoapSamples
{
    /// <summary>Long enough to look like a real EWS folder id; deliberately not one.</summary>
    public const string FolderId = "AAMkADEXAMPLE-FAKE-FOLDER-ID-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz=";

    public const string ChangeKey = "AQAAABYAAAEXAMPLE-FAKE-CHANGE-KEY";

    public static string Success(string folderId = FolderId) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Header>
            <h:ServerVersionInfo MajorVersion="15" MinorVersion="2" MajorBuildNumber="2562" MinorBuildNumber="17" Version="V2018_01_08" xmlns:h="http://schemas.microsoft.com/exchange/services/2006/types" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />
          </s:Header>
          <s:Body xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
            <m:GetFolderResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
              <m:ResponseMessages>
                <m:GetFolderResponseMessage ResponseClass="Success">
                  <m:ResponseCode>NoError</m:ResponseCode>
                  <m:Folders>
                    <t:Folder>
                      <t:FolderId Id="{{folderId}}" ChangeKey="{{ChangeKey}}" />
                      <t:DisplayName>Inbox</t:DisplayName>
                    </t:Folder>
                  </m:Folders>
                </m:GetFolderResponseMessage>
              </m:ResponseMessages>
            </m:GetFolderResponse>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>An HTTP 200 response whose single response message is an error, such as ErrorAccessDenied.</summary>
    public static string Error(string responseCode, string messageText) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
            <m:GetFolderResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
              <m:ResponseMessages>
                <m:GetFolderResponseMessage ResponseClass="Error">
                  <m:MessageText>{{messageText}}</m:MessageText>
                  <m:ResponseCode>{{responseCode}}</m:ResponseCode>
                  <m:DescriptiveLinkKey>0</m:DescriptiveLinkKey>
                  <m:Folders />
                </m:GetFolderResponseMessage>
              </m:ResponseMessages>
            </m:GetFolderResponse>
          </s:Body>
        </s:Envelope>
        """;

    public static string AccessDenied() => Error("ErrorAccessDenied", "Access is denied. Check credentials and try again.");

    /// <summary>A SOAP fault as EWS returns it with HTTP 500.</summary>
    public static string Fault(string ewsCode, string message) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <s:Fault>
              <faultcode xmlns:a="http://schemas.microsoft.com/exchange/services/2006/types">a:{{ewsCode}}</faultcode>
              <faultstring xml:lang="en-US">{{message}}</faultstring>
              <detail>
                <e:ResponseCode xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">{{ewsCode}}</e:ResponseCode>
                <e:Message xmlns:e="http://schemas.microsoft.com/exchange/services/2006/errors">{{message}}</e:Message>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    /// <summary>A sign-in page that is well-formed XML but is not SOAP, as a proxy or ADFS might return.</summary>
    public const string SignInPage = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Sign in</title></head><body>Please sign in.</body></html>";

    public const string EnvelopeWithoutGetFolderResponse = """
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body><m:SomethingElseResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" /></s:Body>
        </s:Envelope>
        """;
}
