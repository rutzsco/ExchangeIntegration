using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ExchangeIntegration.Tests.TestSupport;

/// <summary>
/// A minimal loopback HTTP server over a raw socket. It exists so that tests can exercise the real
/// <see cref="HttpClientHandler"/> (redirect handling, authentication fallback), which a mocked transport replaces.
/// </summary>
public sealed class RawHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<int, string> _respond;
    private readonly ConcurrentQueue<string> _requestHeads = new();
    private readonly Task _acceptLoop;

    /// <param name="respond">Maps the 1-based request number to the complete raw HTTP response to send.</param>
    public RawHttpServer(Func<int, string> respond)
    {
        _respond = respond;
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    /// <summary>The raw request line and headers of every request received so far.</summary>
    public IReadOnlyList<string> RequestHeads => _requestHeads.ToArray();

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends with an exception when the listener stops; nothing to report.
        }

        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Normal shutdown.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, _cancellation.Token);
                if (head is null)
                {
                    return;
                }

                _requestHeads.Enqueue(head);
                var response = Encoding.ASCII.GetBytes(_respond(_requestHeads.Count));
                await stream.WriteAsync(response, _cancellation.Token);
                await stream.FlushAsync(_cancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The client went away or the server is shutting down.
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var head = new StringBuilder();
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            head.Append((char)buffer[0]);
        }

        return head.ToString();
    }
}
