namespace ExchangeIntegration.Tests.TestSupport;

/// <summary>Service-mode EWS responses shaped like those of Exchange Server. Every identifier and address is fake.</summary>
public static class EwsSamples
{
    private const string Open = """
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
        """;

    private const string Close = """
          </s:Body>
        </s:Envelope>
        """;

    private static string Response(string operation, string content, string responseClass = "Success", string code = "NoError") => $$"""
        {{Open}}
            <m:{{operation}}Response xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
              <m:ResponseMessages>
                <m:{{operation}}ResponseMessage ResponseClass="{{responseClass}}">
                  <m:ResponseCode>{{code}}</m:ResponseCode>
                  {{content}}
                </m:{{operation}}ResponseMessage>
              </m:ResponseMessages>
            </m:{{operation}}Response>
        {{Close}}
        """;

    public static string FindItem(bool includesLast, int nextOffset, params (string Id, string ItemClass, string? MessageId)[] items) =>
        Response("FindItem", $$"""
            <m:RootFolder IndexedPagingOffset="{{nextOffset}}" TotalItemsInView="{{items.Length}}" IncludesLastItemInRange="{{(includesLast ? "true" : "false")}}">
              <t:Items>
                {{string.Concat(items.Select(item => $"""
                  <t:Message>
                    <t:ItemId Id="{item.Id}" ChangeKey="CQAAABYAAAEXAMPLE" />
                    <t:ItemClass>{item.ItemClass}</t:ItemClass>
                    <t:DateTimeReceived>2026-11-02T09:00:00Z</t:DateTimeReceived>
                    {(item.MessageId is null ? string.Empty : $"<t:InternetMessageId>{System.Security.SecurityElement.Escape(item.MessageId)}</t:InternetMessageId>")}
                  </t:Message>
                  """))}}
              </t:Items>
            </m:RootFolder>
            """);

    public const string GetItem = """
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <m:GetItemResponse xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages" xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types">
              <m:ResponseMessages>
                <m:GetItemResponseMessage ResponseClass="Success">
                  <m:ResponseCode>NoError</m:ResponseCode>
                  <m:Items>
                    <t:Message>
                      <t:ItemId Id="item-1" ChangeKey="CQAAABYAAAEXAMPLE" />
                      <t:Subject>Invoice 1001</t:Subject>
                      <t:DateTimeReceived>2026-11-02T09:00:00Z</t:DateTimeReceived>
                      <t:Attachments>
                        <t:FileAttachment>
                          <t:AttachmentId Id="att-1" />
                          <t:Name>invoice 1001.pdf</t:Name>
                          <t:ContentType>application/pdf</t:ContentType>
                          <t:Size>1234</t:Size>
                          <t:IsInline>false</t:IsInline>
                        </t:FileAttachment>
                        <t:ItemAttachment>
                          <t:AttachmentId Id="att-2" />
                          <t:Name>Forwarded email</t:Name>
                          <t:Size>999</t:Size>
                        </t:ItemAttachment>
                      </t:Attachments>
                      <t:InternetMessageHeaders>
                        <t:InternetMessageHeader HeaderName="Received">from mx.example.test</t:InternetMessageHeader>
                        <t:InternetMessageHeader HeaderName="auto-submitted">auto-replied</t:InternetMessageHeader>
                      </t:InternetMessageHeaders>
                      <t:From>
                        <t:Mailbox>
                          <t:Name>Supplier</t:Name>
                          <t:EmailAddress>supplier@example.test</t:EmailAddress>
                          <t:RoutingType>SMTP</t:RoutingType>
                        </t:Mailbox>
                      </t:From>
                      <t:InternetMessageId>&lt;inv-1001@example.test&gt;</t:InternetMessageId>
                      <t:References>&lt;earlier@example.test&gt;</t:References>
                    </t:Message>
                  </m:Items>
                </m:GetItemResponseMessage>
              </m:ResponseMessages>
            </m:GetItemResponse>
          </s:Body>
        </s:Envelope>
        """;

    public static string GetAttachment(string base64Content) => Response("GetAttachment", $"""
        <m:Attachments>
          <t:FileAttachment>
            <t:AttachmentId Id="att-1" />
            <t:Name>invoice.pdf</t:Name>
            <t:Content>{base64Content}</t:Content>
          </t:FileAttachment>
        </m:Attachments>
        """);

    public static string CreateItem() => Response("CreateItem", "<m:Items />");

    public static string FindFolder(params (string Id, string DisplayName)[] folders) => Response("FindFolder", $$"""
        <m:RootFolder TotalItemsInView="{{folders.Length}}" IncludesLastItemInRange="true">
          <t:Folders>
            {{string.Concat(folders.Select(folder => $"""<t:Folder><t:FolderId Id="{folder.Id}" /><t:DisplayName>{folder.DisplayName}</t:DisplayName></t:Folder>"""))}}
          </t:Folders>
        </m:RootFolder>
        """);

    public static string MoveItem() => Response("MoveItem", "<m:Items />");

    public static string Error(string operation, string code, string text) =>
        Response(operation, $"<m:MessageText>{text}</m:MessageText>", "Error", code);
}
