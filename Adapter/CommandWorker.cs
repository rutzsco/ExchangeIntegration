using Azure.Messaging.ServiceBus;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Adapter;

/// <summary>
/// Receives Engine commands one at a time. A handled message is completed; a malformed one is dead-lettered; an
/// exception abandons it so Service Bus redelivers it (and dead-letters it after the queue's max delivery count).
/// </summary>
public sealed class CommandWorker : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly ExchangeCommandHandler _handler;
    private readonly AzureOptions _options;
    private readonly ILogger<CommandWorker> _logger;

    public CommandWorker(ServiceBusClient client, ExchangeCommandHandler handler, IOptions<AzureOptions> options, ILogger<CommandWorker> logger)
    {
        _client = client;
        _handler = handler;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var processor = _client.CreateProcessor(
            _options.CommandsQueue,
            new ServiceBusProcessorOptions { MaxConcurrentCalls = 1, AutoCompleteMessages = true });

        processor.ProcessMessageAsync += async args =>
        {
            var outcome = await _handler.HandleAsync(args.Message.Body, args.CancellationToken);
            if (outcome.DeadLetterReason is not null)
            {
                await args.DeadLetterMessageAsync(args.Message, outcome.DeadLetterReason, outcome.DeadLetterDescription, args.CancellationToken);
            }
        };

        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(
                "Command processing error ({Source}, {Queue}): {Error}",
                args.ErrorSource,
                args.EntityPath,
                InvoiceIngestion.Describe(args.Exception));
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Listening for Engine commands on {Queue}.", _options.CommandsQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        await processor.StopProcessingAsync(CancellationToken.None);
        _logger.LogInformation("Stopped listening for Engine commands.");
    }
}
