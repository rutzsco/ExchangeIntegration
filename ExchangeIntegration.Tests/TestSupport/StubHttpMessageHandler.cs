using System.Net;
using System.Text;

namespace ExchangeIntegration.Tests.TestSupport;

public sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? ContentType,
    string Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>A mocked HTTP transport: records every request and answers with whatever the test supplies.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<CapturedRequest> _requests = [];

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public IReadOnlyList<CapturedRequest> Requests => _requests;

    public static StubHttpMessageHandler Returning(
        HttpStatusCode status,
        string body = "",
        string mediaType = "text/xml",
        Action<HttpResponseMessage>? configure = null) =>
        new((_, _) => Task.FromResult(CreateResponse(status, body, mediaType, configure)));

    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new((_, _) => throw exception);

    public static HttpResponseMessage CreateResponse(
        HttpStatusCode status,
        string body = "",
        string mediaType = "text/xml",
        Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        configure?.Invoke(response);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value));

        _requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Content?.Headers.ContentType?.ToString(),
            body,
            headers));

        return await _respond(request, cancellationToken);
    }
}
