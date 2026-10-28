namespace ExchangeIntegration.Exchange;

/// <summary>Read-only Exchange Web Services operations.</summary>
public interface IEwsClient
{
    /// <summary>
    /// Issues a read-only GetFolder for the Inbox of <paramref name="mailbox"/>. Expected failures (HTTP, TLS,
    /// SOAP, EWS error codes, timeout) are returned as a classified result rather than thrown; only caller
    /// cancellation surfaces as an <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<EwsGetFolderResult> GetInboxFolderAsync(string mailbox, CancellationToken cancellationToken = default);
}
