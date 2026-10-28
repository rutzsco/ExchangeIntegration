using System.Net;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Tests.TestSupport;

namespace ExchangeIntegration.Tests;

public class ExchangeHttpClientFactoryTests
{
    [Fact]
    public void Handler_authenticates_with_the_process_identity_and_does_not_follow_redirects()
    {
        using var handler = ExchangeHttpClientFactory.CreateHandler();

        Assert.True(handler.UseDefaultCredentials);
        Assert.Same(CredentialCache.DefaultCredentials, handler.Credentials);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void Handler_leaves_tls_certificate_validation_untouched()
    {
        using var handler = ExchangeHttpClientFactory.CreateHandler();

        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void Client_applies_the_configured_timeout_user_agent_and_response_limit()
    {
        var options = new ExchangeOptions { TimeoutSeconds = 45 };

        using var client = ExchangeHttpClientFactory.Create(options, StubHttpMessageHandler.Returning(HttpStatusCode.OK));

        Assert.Equal(TimeSpan.FromSeconds(45), client.Timeout);
        Assert.Equal(ExchangeHttpClientFactory.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(ExchangeHttpClientFactory.MaxResponseBytes, client.MaxResponseContentBufferSize);
    }

    [Fact]
    public void Client_never_carries_a_preconfigured_authorization_header()
    {
        using var client = ExchangeHttpClientFactory.Create(new ExchangeOptions(), StubHttpMessageHandler.Returning(HttpStatusCode.OK));

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    // The next tests use the real handler, which a mocked transport would replace, against a loopback server.

    [Fact]
    public async Task Real_handler_does_not_follow_a_redirect()
    {
        using var server = new RawHttpServer(requestNumber => requestNumber == 1
            ? "HTTP/1.1 302 Found\r\nLocation: /elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = ExchangeHttpClientFactory.Create(new ExchangeOptions { TimeoutSeconds = 10 });

        using var response = await client.GetAsync(server.BaseAddress);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(server.RequestHeads);
    }

    [Theory]
    [InlineData("Basic realm=\"test\"")]
    [InlineData("Digest realm=\"test\", nonce=\"0123456789abcdef\", qop=\"auth\"")]
    public async Task Real_handler_never_answers_a_basic_or_digest_challenge(string challenge)
    {
        using var server = new RawHttpServer(_ =>
            $"HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: {challenge}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = ExchangeHttpClientFactory.Create(new ExchangeOptions { TimeoutSeconds = 10 });

        using var response = await client.GetAsync(server.BaseAddress);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var requests = server.RequestHeads;
        Assert.Single(requests);
        Assert.DoesNotContain("Authorization:", requests[0], StringComparison.OrdinalIgnoreCase);
    }
}
