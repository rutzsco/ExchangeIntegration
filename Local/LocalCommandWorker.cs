using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Local;

/// <summary>
/// The command listener of local mode. Every two seconds it hands each *.json file in the commands folder to the
/// handler, one at a time, then moves it to processed/. A malformed command, or one that keeps failing, goes to
/// dead-letter/ (after 10 attempts, like the Service Bus max delivery count).
/// </summary>
public sealed class LocalCommandWorker : BackgroundService
{
    public const string ProcessedFolder = "processed";
    public const string DeadLetterFolder = "dead-letter";
    public const int MaxDeliveryCount = 10;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ExchangeCommandHandler _handler;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger<LocalCommandWorker> _logger;
    private readonly Dictionary<string, int> _failedAttempts = new(StringComparer.OrdinalIgnoreCase);

    public LocalCommandWorker(
        ExchangeCommandHandler handler,
        LocalFolders folders,
        IOptions<AzureOptions> options,
        TimeProvider time,
        ILogger<LocalCommandWorker> logger)
    {
        _handler = handler;
        _folder = folders.Queue(options.Value.CommandsQueue);
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_folder);
        _logger.LogInformation("Watching {Folder} for Engine command files (*.json).", _folder);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(_folder, "*.json").Order(StringComparer.Ordinal).ToList())
                {
                    await ProcessAsync(path, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError("Reading the commands folder failed; retrying: {Error}", InvoiceIngestion.Describe(exception));
            }

            await Task.Delay(PollInterval, _time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _logger.LogInformation("Stopped watching for Engine command files.");
    }

    private async Task ProcessAsync(string path, CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            // Still being written (or just removed); try again next time.
            return;
        }

        var file = Path.GetFileName(path);
        try
        {
            var outcome = await _handler.HandleAsync(BinaryData.FromBytes(body), cancellationToken);
            if (outcome.DeadLetterReason is null)
            {
                MoveTo(path, ProcessedFolder);
            }
            else
            {
                _logger.LogWarning("Command file {File} moved to {Folder}: {Reason}: {Description}", file, DeadLetterFolder, outcome.DeadLetterReason, outcome.DeadLetterDescription);
                MoveTo(path, DeadLetterFolder);
            }

            _failedAttempts.Remove(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var attempts = _failedAttempts[path] = _failedAttempts.GetValueOrDefault(path) + 1;
            if (attempts < MaxDeliveryCount)
            {
                _logger.LogError("Command file {File} failed (attempt {Attempt} of {Max}); retrying: {Error}", file, attempts, MaxDeliveryCount, InvoiceIngestion.Describe(exception));
                return;
            }

            _logger.LogError("Command file {File} failed {Max} times; moving it to {Folder}: {Error}", file, MaxDeliveryCount, DeadLetterFolder, InvoiceIngestion.Describe(exception));
            MoveTo(path, DeadLetterFolder);
            _failedAttempts.Remove(path);
        }
    }

    private void MoveTo(string path, string subfolder)
    {
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(_folder, subfolder)).FullName;
            var name = Path.GetFileNameWithoutExtension(path);
            var target = Path.Combine(folder, Path.GetFileName(path));
            for (var copy = 1; File.Exists(target); copy++)
            {
                target = Path.Combine(folder, $"{name}-{copy}.json");
            }

            File.Move(path, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Handled again next time; a repeated command returns its recorded result.
            _logger.LogWarning("Command file {File} could not be moved to {Folder}: {Error}", Path.GetFileName(path), subfolder, exception.Message);
        }
    }
}
