using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using ExchangeIntegration.Adapter;
using ExchangeIntegration.Authentication;
using ExchangeIntegration.Cloud;
using ExchangeIntegration.Diagnostics;
using ExchangeIntegration.Exchange;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeIntegration.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Exchange settings (validated at startup), the Windows-authenticated EWS client and the
    /// connectivity probe. Service mode uses <see cref="AddExchangeAdapter"/> instead.
    /// </summary>
    public static IServiceCollection AddExchangeIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<ExchangeOptions>, ExchangeOptionsValidator>();
        services.AddOptions<ExchangeOptions>()
            .Bind(configuration.GetSection(ExchangeOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IProcessIdentityProvider, WindowsProcessIdentityProvider>();

        // One long-lived client: NTLM authenticates the connection, so connection reuse avoids repeated handshakes.
        services.AddSingleton(provider => ExchangeHttpClientFactory.Create(provider.GetRequiredService<IOptions<ExchangeOptions>>().Value));
        services.AddSingleton<IEwsClient, EwsClient>();
        services.AddSingleton<ExchangeConnectivityProbe>();

        return services;
    }

    /// <summary>
    /// Service mode (--run): one EWS client and poller per mailbox, shared Azure clients and one command listener.
    /// </summary>
    public static IServiceCollection AddExchangeAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var exchange = configuration.GetSection(ExchangeOptions.SectionName);
        var mailboxList = exchange.GetSection("Mailboxes");
        var mailboxSections = mailboxList.GetChildren().ToArray();
        if (mailboxSections.Length == 0)
        {
            mailboxSections = [exchange];
        }

        services.AddSingleton<IValidateOptions<ExchangeMailboxesOptions>, ExchangeMailboxesOptionsValidator>();
        services.AddOptions<ExchangeMailboxesOptions>()
            .Configure(options =>
            {
                if (!string.IsNullOrEmpty(mailboxList.Value))
                {
                    throw new OptionsValidationException(
                        Options.DefaultName, typeof(ExchangeMailboxesOptions), ["Exchange:Mailboxes must be an array of mailbox configurations."]);
                }

                options.ConfigurationPaths = mailboxSections.Select(section => section.Path).ToArray();
                try
                {
                    options.Mailboxes = mailboxSections.Select(section => section.Get<ExchangeOptions>() ?? new()).ToArray();
                }
                catch (InvalidOperationException exception)
                {
                    throw new OptionsValidationException(Options.DefaultName, typeof(ExchangeMailboxesOptions), [exception.Message]);
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<AzureOptions>, AzureOptionsValidator>();
        services.AddOptions<AzureOptions>()
            .Bind(configuration.GetSection(AzureOptions.SectionName))
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton(provider =>
        {
            var azure = provider.GetRequiredService<IOptions<AzureOptions>>().Value;
            return new BlobServiceClient(new Uri(azure.BlobServiceUri), provider.GetRequiredService<TokenCredential>())
                .GetBlobContainerClient(azure.IntakeContainer);
        });

        services.AddSingleton(provider =>
        {
            var azure = provider.GetRequiredService<IOptions<AzureOptions>>().Value;
            return new ServiceBusClient(
                azure.ServiceBusNamespace,
                provider.GetRequiredService<TokenCredential>(),
                new ServiceBusClientOptions
                {
                    TransportType = azure.UseWebSockets ? ServiceBusTransportType.AmqpWebSockets : ServiceBusTransportType.AmqpTcp,
                });
        });

        services.AddSingleton<IInvoiceStore, BlobInvoiceStore>();
        services.AddSingleton<IEngineQueues, ServiceBusEngineQueues>();
        for (var index = 0; index < mailboxSections.Length; index++)
        {
            var mailboxIndex = index;
            services.AddKeyedSingleton<HttpClient>(mailboxIndex, (provider, _) =>
                ExchangeHttpClientFactory.CreateForAdapter(
                    provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value.Mailboxes[mailboxIndex]));

            services.AddKeyedSingleton<ConfiguredMailbox>(mailboxIndex, (provider, _) =>
            {
                var options = provider.GetRequiredService<IOptions<ExchangeMailboxesOptions>>().Value.Mailboxes[mailboxIndex];
                return new ConfiguredMailbox(
                    options,
                    new EwsMailbox(
                        provider.GetRequiredKeyedService<HttpClient>(mailboxIndex),
                        Options.Create(options),
                        provider.GetRequiredService<ILogger<EwsMailbox>>()));
            });
            services.AddSingleton(provider => provider.GetRequiredKeyedService<ConfiguredMailbox>(mailboxIndex));

            services.AddKeyedSingleton<InvoiceIngestion>(mailboxIndex, (provider, _) =>
            {
                var mailbox = provider.GetRequiredKeyedService<ConfiguredMailbox>(mailboxIndex);
                return new InvoiceIngestion(
                    mailbox.Client,
                    provider.GetRequiredService<IInvoiceStore>(),
                    provider.GetRequiredService<IEngineQueues>(),
                    Options.Create(mailbox.Options),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<InvoiceIngestion>>());
            });

            // AddHostedService deduplicates by type; register every mailbox's worker explicitly.
            services.AddSingleton<IHostedService>(provider => new IngestionWorker(
                provider.GetRequiredKeyedService<InvoiceIngestion>(mailboxIndex),
                Options.Create(provider.GetRequiredKeyedService<ConfiguredMailbox>(mailboxIndex).Options),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<IngestionWorker>>()));
        }

        services.AddSingleton<ExchangeCommandHandler>();
        services.AddHostedService<CommandWorker>();

        return services;
    }
}
