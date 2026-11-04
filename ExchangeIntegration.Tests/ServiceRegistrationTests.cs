using Azure.Storage.Blobs;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Cloud;
using ExchangeIntegration.Configuration;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using ExchangeIntegration.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Tests;

/// <summary>Sets environment variables for the lifetime of a test and restores the originals afterwards.</summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originals = [];

    public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _originals[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, original) in _originals)
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public class ServiceRegistrationTests
{
    private static readonly Dictionary<string, string?> NoExchangeVariables = new()
    {
        ["Exchange__EwsUrl"] = null,
        ["Exchange__Mailbox"] = null,
        ["Exchange__AccessMode"] = null,
        ["Exchange__TimeoutSeconds"] = null,
        ["Exchange__PollIntervalSeconds"] = null,
        ["Exchange__MaxMessagesPerPoll"] = null,
        ["Exchange__MoveFolders__0"] = null,
        ["Azure__BlobServiceUri"] = null,
        ["Azure__IntakeContainer"] = null,
        ["Azure__ServiceBusNamespace"] = null,
        ["Azure__InvoiceReceivedQueue"] = null,
        ["Azure__CommandsQueue"] = null,
        ["Azure__CommandResultsQueue"] = null,
        ["Azure__UseWebSockets"] = null,
    };

    private static readonly Dictionary<string, string?> ValidServiceSettings = new()
    {
        ["Exchange:EwsUrl"] = "https://mail.contoso.com/EWS/Exchange.asmx",
        ["Exchange:Mailbox"] = "ap-invoices@contoso.com",
        ["Azure:BlobServiceUri"] = "https://contosoinvoices.blob.core.windows.net",
        ["Azure:ServiceBusNamespace"] = "contoso-invoices.servicebus.windows.net",
    };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings, bool serviceMode = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (serviceMode)
        {
            services.AddExchangeAdapter(configuration);
        }
        else
        {
            services.AddExchangeIntegration(configuration);
        }

        return services.BuildServiceProvider();
    }

    // The same call Program.cs uses, so these tests cover appsettings.json, environment variables and arguments.
    private static IHost BuildHost(params string[] args) => BuildHost(serviceMode: false, args);

    private static IHost BuildHost(bool serviceMode, params string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        if (serviceMode)
        {
            builder.Services.AddExchangeAdapter(builder.Configuration);
        }
        else
        {
            builder.Services.AddExchangeIntegration(builder.Configuration);
        }

        return builder.Build();
    }

    [Fact]
    public async Task Service_mode_resolves_both_workers_and_the_azure_adapters()
    {
        await using var provider = BuildProvider(ValidServiceSettings, serviceMode: true);

        var workers = provider.GetServices<IHostedService>().ToList();

        Assert.Contains(workers, worker => worker is IngestionWorker);
        Assert.Contains(workers, worker => worker is CommandWorker);
        Assert.IsType<EwsMailbox>(Assert.Single(provider.GetServices<ConfiguredMailbox>()).Client);
        Assert.IsType<BlobInvoiceStore>(provider.GetRequiredService<IInvoiceStore>());
        Assert.IsType<ServiceBusEngineQueues>(provider.GetRequiredService<IEngineQueues>());
        Assert.Equal("invoice-intake", provider.GetRequiredService<BlobContainerClient>().Name);
    }

    [Fact]
    public async Task Service_mode_requires_the_azure_settings()
    {
        await using var provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Exchange:EwsUrl"] = "https://mail.contoso.com/EWS/Exchange.asmx",
                ["Exchange:Mailbox"] = "ap-invoices@contoso.com",
            },
            serviceMode: true);

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AzureOptions>>().Value);

        Assert.Equal(2, exception.Failures.Count());
    }

    [Fact]
    public void Probe_mode_registers_no_workers()
    {
        using var provider = BuildProvider(ValidServiceSettings);

        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void Probe_mode_ignores_invalid_service_mailbox_entries()
    {
        using var provider = BuildProvider(new(ValidServiceSettings)
        {
            ["Exchange:Mailboxes:0:Mailbox"] = "not an address",
            ["Exchange:Mailboxes:0:TimeoutSeconds"] = "not a number",
        });

        Assert.Equal("ap-invoices@contoso.com", provider.GetRequiredService<IOptions<ExchangeOptions>>().Value.Mailbox);
        Assert.NotNull(provider.GetRequiredService<ExchangeConnectivityProbe>());
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public void Indexed_environment_variables_and_arguments_configure_multiple_mailboxes()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__Mailboxes__0__Mailbox"] = "first@example.test",
            ["Exchange__Mailboxes__0__EwsUrl"] = "https://first.example.test/EWS/Exchange.asmx",
            ["Exchange__Mailboxes__1__Mailbox"] = "second@example.test",
            ["Exchange__Mailboxes__1__EwsUrl"] = "https://second.example.test/EWS/Exchange.asmx",
            ["Exchange__Mailboxes__1__PollIntervalSeconds"] = "120",
        });
        using var host = BuildHost(serviceMode: true, "--Exchange:Mailboxes:1:PollIntervalSeconds=30");

        var mailboxes = host.Services.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value.Mailboxes;

        Assert.Equal(["first@example.test", "second@example.test"], mailboxes.Select(mailbox => mailbox.Mailbox));
        Assert.Equal(60, mailboxes[0].PollIntervalSeconds);
        Assert.Equal(30, mailboxes[1].PollIntervalSeconds);
    }

    [Fact]
    public void The_shipped_appsettings_json_binds_to_valid_service_mode_options()
    {
        using var environment = new EnvironmentVariableScope(NoExchangeVariables);
        using var host = BuildHost(serviceMode: true);

        var exchange = Assert.Single(host.Services.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value.Mailboxes);
        var azure = host.Services.GetRequiredService<IOptions<AzureOptions>>().Value;

        Assert.Empty(AzureOptionsValidator.GetFailures(azure));
        Assert.Equal(["Processed"], exchange.GetMoveFolders());
        Assert.Equal(ExchangeOptions.DefaultPollIntervalSeconds, exchange.PollIntervalSeconds);
        Assert.Equal("exchange-commands", azure.CommandsQueue);
        Assert.False(azure.UseWebSockets);
    }

    [Fact]
    public void Valid_settings_resolve_the_probe_with_the_windows_authenticated_client()
    {
        using var provider = BuildProvider(new()
        {
            ["Exchange:EwsUrl"] = "https://mail.contoso.com/EWS/Exchange.asmx",
            ["Exchange:Mailbox"] = "ap-invoices@contoso.com",
        });

        Assert.NotNull(provider.GetRequiredService<ExchangeConnectivityProbe>());
        Assert.IsType<EwsClient>(provider.GetRequiredService<IEwsClient>());
        Assert.IsType<WindowsProcessIdentityProvider>(provider.GetRequiredService<IProcessIdentityProvider>());
    }

    [Fact]
    public void Invalid_settings_fail_validation_as_soon_as_the_options_are_read()
    {
        using var provider = BuildProvider(new()
        {
            ["Exchange:EwsUrl"] = "http://mail.contoso.com/EWS/Exchange.asmx",
            ["Exchange:Mailbox"] = "not an address",
        });

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeOptions>>().Value);

        Assert.True(exception.Failures.Count() >= 2);
    }

    [Fact]
    public void Missing_settings_fail_validation()
    {
        using var provider = BuildProvider([]);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ExchangeOptions>>().Value);
    }

    [Fact]
    public void The_shipped_appsettings_json_binds_to_valid_options()
    {
        using var environment = new EnvironmentVariableScope(NoExchangeVariables);
        using var host = BuildHost();

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Empty(ExchangeOptionsValidator.GetFailures(options));
        Assert.Equal(ExchangeAccessMode.Delegated, options.AccessMode);
        Assert.Equal(ExchangeOptions.DefaultTimeoutSeconds, options.TimeoutSeconds);
        Assert.StartsWith("https://", options.EwsUrl);
    }

    [Fact]
    public void Environment_variables_override_appsettings_json_without_code_changes()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__EwsUrl"] = "https://exchange.example.test/EWS/Exchange.asmx",
            ["Exchange__Mailbox"] = "approved-test@example.test",
            ["Exchange__TimeoutSeconds"] = "12",
        });
        using var host = BuildHost();

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Equal("https://exchange.example.test/EWS/Exchange.asmx", options.EwsUrl);
        Assert.Equal("approved-test@example.test", options.Mailbox);
        Assert.Equal(12, options.TimeoutSeconds);
    }

    [Fact]
    public void Command_line_arguments_override_the_other_sources()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__Mailbox"] = "from-environment@example.test",
        });
        using var host = BuildHost("--Exchange:Mailbox=from-arguments@example.test");

        var options = host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value;

        Assert.Equal("from-arguments@example.test", options.Mailbox);
    }

    [Fact]
    public void A_value_that_cannot_be_converted_surfaces_as_a_configuration_error()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__AccessMode"] = "NotAMode",
        });
        using var host = BuildHost();

        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value);
    }

    [Fact]
    public void Impersonation_is_rejected_by_startup_validation()
    {
        using var environment = new EnvironmentVariableScope(new Dictionary<string, string?>(NoExchangeVariables)
        {
            ["Exchange__AccessMode"] = "Impersonation",
        });
        using var host = BuildHost();

        var exception = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ExchangeOptions>>().Value);

        Assert.Contains(exception.Failures, failure => failure.Contains("not supported in this milestone"));
    }
}
