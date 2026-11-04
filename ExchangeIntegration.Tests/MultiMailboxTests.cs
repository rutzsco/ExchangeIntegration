using System.Net;
using System.Threading.Channels;
using System.Xml.Linq;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

public class MultiMailboxTests
{
    private static Dictionary<string, string?> Settings(int count = 2)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Azure:BlobServiceUri"] = "https://contosoinvoices.blob.core.windows.net",
            ["Azure:ServiceBusNamespace"] = "contoso-invoices.servicebus.windows.net",
            // A non-empty mailbox list replaces the legacy section; these must not be used or inherited.
            ["Exchange:EwsUrl"] = "http://legacy.example.test",
            ["Exchange:Mailbox"] = "not an address",
            ["Exchange:TimeoutSeconds"] = "0",
            ["Exchange:PollIntervalSeconds"] = "0",
            ["Exchange:MoveFolders:0"] = "Legacy",
        };
        for (var index = 0; index < count; index++)
        {
            settings[$"Exchange:Mailboxes:{index}:Mailbox"] = $"invoices-{index}@contoso.com";
            settings[$"Exchange:Mailboxes:{index}:EwsUrl"] = $"https://mail-{index}.contoso.com/EWS/Exchange.asmx";
        }

        return settings;
    }

    private static ServiceCollection Services(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExchangeAdapter(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
        services.AddSingleton<IEngineQueues, RecordingEngineQueues>();
        return services;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(25)]
    public async Task Every_configured_mailbox_has_its_own_client_and_worker_with_one_command_listener(int count)
    {
        var settings = Settings(count);
        settings["Exchange:Mailboxes:0:TimeoutSeconds"] = "12";
        settings["Exchange:Mailboxes:0:PollIntervalSeconds"] = "15";
        settings["Exchange:Mailboxes:0:MaxMessagesPerPoll"] = "7";
        settings["Exchange:Mailboxes:0:MoveFolders:0"] = "Reviewed";
        await using var provider = Services(settings).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value;
        var mailboxes = provider.GetServices<ConfiguredMailbox>().ToArray();
        var workers = provider.GetServices<IHostedService>().ToArray();

        Assert.Equal(count, options.Mailboxes.Length);
        Assert.Equal(count, mailboxes.Length);
        Assert.Equal(count, mailboxes.Select(mailbox => mailbox.Client).Distinct().Count());
        Assert.Equal(count, workers.OfType<IngestionWorker>().Count());
        Assert.Single(workers.OfType<CommandWorker>());
        for (var index = 0; index < count; index++)
        {
            Assert.Same(options.Mailboxes[index], mailboxes[index].Options);
            Assert.Equal($"invoices-{index}@contoso.com", mailboxes[index].Options.Mailbox);
            Assert.Equal($"https://mail-{index}.contoso.com/EWS/Exchange.asmx", mailboxes[index].Options.EwsUrl);
            Assert.Equal(TimeSpan.FromSeconds(index == 0 ? 12 : 30), provider.GetRequiredKeyedService<HttpClient>(index).Timeout);
            Assert.Equal(index == 0 ? 15 : 60, mailboxes[index].Options.PollIntervalSeconds);
            Assert.Equal(index == 0 ? 7 : 20, mailboxes[index].Options.MaxMessagesPerPoll);
            Assert.Equal([index == 0 ? "Reviewed" : "Processed"], mailboxes[index].Options.GetMoveFolders());
        }
    }

    [Fact]
    public async Task All_mailboxes_are_validated_and_duplicate_addresses_are_rejected_case_insensitively()
    {
        var settings = Settings(3);
        settings["Exchange:Mailboxes:0:EwsUrl"] = "http://mail.contoso.com";
        settings["Exchange:Mailboxes:1:Mailbox"] = "INVOICES-0@CONTOSO.COM";
        settings["Exchange:Mailboxes:1:PollIntervalSeconds"] = "1";
        settings["Exchange:Mailboxes:2:TimeoutSeconds"] = "301";
        settings["Exchange:Mailboxes:2:MoveFolders:0"] = "Inbox/Processed";
        await using var provider = Services(settings).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value);

        Assert.Equal(5, exception.Failures.Count());
        Assert.Contains(exception.Failures, failure => failure.StartsWith("Exchange:Mailboxes:0:EwsUrl"));
        Assert.Contains(exception.Failures, failure => failure.StartsWith("Exchange:Mailboxes:1:Mailbox duplicates"));
        Assert.Contains(exception.Failures, failure => failure.StartsWith("Exchange:Mailboxes:1:PollIntervalSeconds"));
        Assert.Contains(exception.Failures, failure => failure.StartsWith("Exchange:Mailboxes:2:TimeoutSeconds"));
        Assert.Contains(exception.Failures, failure => failure.StartsWith("Exchange:Mailboxes:2:MoveFolders"));
    }

    [Fact]
    public async Task An_entry_cannot_inherit_a_missing_endpoint_from_the_legacy_section()
    {
        var settings = Settings();
        settings["Exchange:EwsUrl"] = "https://legacy.contoso.com/EWS/Exchange.asmx";
        settings.Remove("Exchange:Mailboxes:1:EwsUrl");
        await using var provider = Services(settings).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value);

        Assert.StartsWith("Exchange:Mailboxes:1:EwsUrl is required", Assert.Single(exception.Failures));
    }

    [Fact]
    public async Task Validation_keeps_the_original_configuration_path_for_sparse_indices()
    {
        var settings = Settings(1);
        settings["Exchange:Mailboxes:5:Mailbox"] = "invoices-5@contoso.com";
        await using var provider = Services(settings).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value);

        Assert.StartsWith("Exchange:Mailboxes:5:EwsUrl is required", Assert.Single(exception.Failures));
    }

    [Theory]
    [InlineData("Exchange:Mailboxes:1:TimeoutSeconds", "not a number")]
    [InlineData("Exchange:Mailboxes", "not a list")]
    public async Task Malformed_list_settings_surface_as_configuration_errors(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        await using var provider = Services(settings).BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains(key));
    }

    [Fact]
    public async Task Invalid_mailboxes_stop_the_host_before_any_ews_requests()
    {
        var settings = Settings();
        settings["Exchange:Mailboxes:1:Mailbox"] = "";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddExchangeAdapter(builder.Configuration);
        var transport = StubHttpMessageHandler.Returning(HttpStatusCode.OK, EwsSamples.FindItem(true, 0));
        builder.Services.AddKeyedSingleton<HttpClient>(0, (_, _) => new HttpClient(transport));
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Each_client_targets_its_own_endpoint_mailbox_and_folder_cache()
    {
        var services = Services(Settings());
        var transports = Enumerable.Range(0, 2).Select(index => new StubHttpMessageHandler(async (request, token) =>
            StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK,
                XDocument.Parse(await request.Content!.ReadAsStringAsync(token)).Descendants()
                    .Any(element => element.Name.LocalName == "FindFolder")
                    ? EwsSamples.FindFolder(($"folder-{index}", "Processed"))
                    : EwsSamples.MoveItem()))).ToArray();
        for (var index = 0; index < transports.Length; index++)
        {
            var transport = transports[index];
            services.AddKeyedSingleton<HttpClient>(index, (_, _) => new HttpClient(transport));
        }
        await using var provider = services.BuildServiceProvider();
        var mailboxes = provider.GetServices<ConfiguredMailbox>().ToArray();
        XNamespace types = "http://schemas.microsoft.com/exchange/services/2006/types";

        for (var index = 0; index < mailboxes.Length; index++)
        {
            await mailboxes[index].Client.MoveAsync(["item-1"], "Processed", CancellationToken.None);
            await mailboxes[index].Client.MoveAsync(["item-2"], "Processed", CancellationToken.None);

            var requests = transports[index].Requests;
            Assert.Equal(3, requests.Count);
            Assert.All(requests, request => Assert.Equal(mailboxes[index].Options.EwsUrl, request.Uri!.AbsoluteUri));
            Assert.Equal(mailboxes[index].Options.Mailbox,
                XDocument.Parse(requests[0].Body).Descendants(types + "EmailAddress").Single().Value);
            Assert.All(requests.Skip(1), request => Assert.Equal($"folder-{index}",
                (string?)XDocument.Parse(request.Body).Descendants(types + "FolderId").Single().Attribute("Id")));
        }
    }

    [Fact]
    public async Task A_failed_poller_retries_on_its_own_interval_without_stopping_another_mailbox()
    {
        var settings = Settings();
        settings["Exchange:Mailboxes:0:PollIntervalSeconds"] = "5";
        settings["Exchange:Mailboxes:1:PollIntervalSeconds"] = "11";
        var services = Services(settings);
        var time = new PollingTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        var failed = StubHttpMessageHandler.Returning(HttpStatusCode.ServiceUnavailable);
        var healthy = StubHttpMessageHandler.Returning(HttpStatusCode.OK, EwsSamples.FindItem(true, 0));
        services.AddKeyedSingleton<HttpClient>(0, (_, _) => new HttpClient(failed));
        services.AddKeyedSingleton<HttpClient>(1, (_, _) => new HttpClient(healthy));
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<IngestionWorker>().ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.StartAsync(timeout.Token)));
            var first = await time.Timers.Reader.ReadAsync(timeout.Token);
            var second = await time.Timers.Reader.ReadAsync(timeout.Token);
            var timers = new[] { first, second };
            Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(11)], timers.Select(timer => timer.DueTime).Order());
            Assert.Single(failed.Requests);
            Assert.Single(healthy.Requests);

            timers.Single(timer => timer.DueTime == TimeSpan.FromSeconds(5)).Fire();
            Assert.Equal(TimeSpan.FromSeconds(5), (await time.Timers.Reader.ReadAsync(timeout.Token)).DueTime);
            Assert.Equal(2, failed.Requests.Count);
            Assert.Single(healthy.Requests);
        }
        finally
        {
            await Task.WhenAll(workers.Select(worker => worker.StopAsync(CancellationToken.None)));
        }

        Assert.All(workers, worker => Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task A_stalled_mailbox_does_not_block_another_mailbox_and_is_cancelled_on_shutdown()
    {
        var services = Services(Settings());
        var stalledEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new StubHttpMessageHandler(async (_, token) =>
        {
            stalledEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, EwsSamples.FindItem(true, 0));
        });
        var healthy = new StubHttpMessageHandler((_, _) =>
        {
            healthyEntered.TrySetResult();
            return Task.FromResult(StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, EwsSamples.FindItem(true, 0)));
        });
        services.AddKeyedSingleton<HttpClient>(0, (_, _) => new HttpClient(stalled));
        services.AddKeyedSingleton<HttpClient>(1, (_, _) => new HttpClient(healthy));
        await using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().OfType<IngestionWorker>().ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.StartAsync(timeout.Token)));
            await Task.WhenAll(stalledEntered.Task, healthyEntered.Task).WaitAsync(timeout.Token);
        }
        finally
        {
            await Task.WhenAll(workers.Select(worker => worker.StopAsync(CancellationToken.None)));
        }

        Assert.All(workers, worker => Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task Disposing_the_container_disposes_every_mailbox_http_client()
    {
        var provider = Services(Settings()).BuildServiceProvider();
        var clients = Enumerable.Range(0, 2).Select(index => provider.GetRequiredKeyedService<HttpClient>(index)).ToArray();

        await provider.DisposeAsync();

        foreach (var client in clients)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("https://unused.example.test"));
        }
    }

    private sealed class PollingTimeProvider : TimeProvider
    {
        public Channel<PollTimer> Timers { get; } = Channel.CreateUnbounded<PollTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PollTimer(callback, state, dueTime);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class PollTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private bool _disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;

        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            DueTime = newDueTime;
            return !_disposed;
        }

        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
