using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Adapter;

/// <summary>Polls the Inbox every Exchange:PollIntervalSeconds until the service stops. A failed poll is logged and retried.</summary>
public sealed class IngestionWorker : BackgroundService
{
    private readonly InvoiceIngestion _ingestion;
    private readonly TimeSpan _interval;
    private readonly string _mailbox;
    private readonly TimeProvider _time;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(InvoiceIngestion ingestion, IOptions<ExchangeOptions> options, TimeProvider time, ILogger<IngestionWorker> logger)
    {
        _ingestion = ingestion;
        _interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        _mailbox = options.Value.Mailbox;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _logger.BeginScope("Mailbox {Mailbox}", _mailbox);
        _logger.LogInformation("Inbox polling started; interval {Interval}.", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _ingestion.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Inbox poll failed for {Mailbox}; retrying in {Interval}: {Error}",
                    _mailbox, _interval, InvoiceIngestion.Describe(exception));
            }

            await Task.Delay(_interval, _time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _logger.LogInformation("Inbox polling stopped.");
    }
}
